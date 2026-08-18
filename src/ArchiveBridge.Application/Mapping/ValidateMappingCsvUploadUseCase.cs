using System.Text;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Mapping;

/// <summary>
/// Entrada CONCEITUAL do backend de validação de upload de mapping. Sem qualquer tipo de ASP.NET: recebe
/// um <see cref="Stream"/> não confiável e metadados. Tenant/projeto, usuário e correlação serão
/// preenchidos server-side pelo Control Plane num incremento posterior.
/// </summary>
public sealed record MappingCsvUploadRequest(
    TenantScope Scope,
    WaveId WaveId,
    ContentCodePage ContentCodePage,
    Stream Content,
    string FileName,
    long? DeclaredLength,
    Guid UserId,
    string RequestedBy,
    Guid IdempotencyKey,
    CorrelationId Correlation);

/// <summary>
/// Resultado seguro (sem PII, sem bytes brutos) de uma validação de upload: o identificador durável, se
/// foi criado/replay, o desfecho e os problemas estruturados persistidos.
/// </summary>
public sealed record MappingUploadValidationOutcome(
    Guid ValidationId,
    bool Created,
    bool Replayed,
    bool IsValid,
    MappingValidationAttemptOutcome Outcome,
    Sha256Hash ContentSha256,
    long SizeBytes,
    int? RowCount,
    int IssueCount,
    bool IssuesTruncated,
    IReadOnlyList<MappingValidationIssue> Issues);

/// <summary>
/// Backend SEGURO de recebimento e validação de CSV de mapping (Passo 6A). Recebe um stream não confiável
/// de forma LIMITADA pelo conteúdo real (nunca pelo Content-Length), calcula o SHA-256 dos BYTES EXATOS,
/// decodifica em UTF-8 ESTRITO SEM BOM, valida contra a onda AUTORIZADA (Approved/Frozen resolvida
/// server-side) reutilizando o <see cref="MappingCsvValidator"/> existente, e persiste uma tentativa de
/// validação DURÁVEL, append-only, tenant/projeto-scoped e idempotente. Não importa nada para o
/// Microsoft 365 e não retém os bytes brutos — a custódia guarda o SHA e os metadados.
/// </summary>
public sealed class ValidateMappingCsvUploadUseCase(
    IWaveStore waves, IMappingValidationStore store, IClock clock, MappingUploadLimits limits)
{
    private const int MaxDisplayFileNameLength = 260;
    private const int MaxRequestedByLength = 200;
    private const string CsvExtension = ".csv";

    private readonly IWaveStore _waves = waves;
    private readonly IMappingValidationStore _store = store;
    private readonly IClock _clock = clock;
    private readonly MappingUploadLimits _limits = limits;

    /// <summary>Recebe, valida e custodia o upload. Reutiliza o schema/parser/validator/policy existentes.</summary>
    public async Task<MappingUploadValidationOutcome> ExecuteAsync(
        MappingCsvUploadRequest request, MappingPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);
        ArgumentNullException.ThrowIfNull(policy);
        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId é obrigatório.", nameof(request));
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(request));
        }

        // (§6) RequestedBy é METADADO OPERACIONAL: validado e normalizado ANTES de qualquer custódia
        // (fail-closed, zero writes em entrada inválida). O valor normalizado é o que se persiste.
        var requestedBy = NormalizeRequestedBy(request.RequestedBy);

        // (§10-11) Extensão + basename: nome do cliente é DADO não confiável, jamais caminho físico.
        var displayName = NormalizeDisplayFileName(request.FileName);

        // (§8/§48) Preflight pelo comprimento declarado — apenas uma dica; nunca a fonte de verdade. Um
        // comprimento NEGATIVO é entrada inválida e é rejeitado imediatamente (stream não lido, zero custódia).
        if (request.DeclaredLength is { } declared)
        {
            if (declared < 0)
            {
                throw new MappingUploadRejectedException(MappingUploadRejectionReason.InvalidDeclaredLength);
            }

            if (declared > _limits.EffectiveMaxUploadBytes)
            {
                throw new MappingUploadRejectedException(MappingUploadRejectionReason.DeclaredLengthTooLarge);
            }
        }

        // (§8) Leitura LIMITADA pelo conteúdo real (limit + 1). Oversized ⇒ rejeição pré-custódia (§9).
        var bytes = await ReadBoundedAsync(request.Content, _limits.EffectiveMaxUploadBytes, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new MappingUploadRejectedException(MappingUploadRejectionReason.ContentTooLarge);

        // (§12) SHA-256 dos BYTES EXATOS recebidos — evidência de custódia (nunca sobre texto reserializado).
        var contentSha = DeterministicHash.ComputeBytes(bytes);
        var sizeBytes = (long)bytes.Length;

        // (§6/§7) Onda AUTORIZADA resolvida server-side. Inexistente/outro escopo ⇒ NotFound (anti-IDOR);
        // estado não Approved/Frozen ⇒ precondição. Ambas pré-custódia: zero tentativa.
        var wave = await _waves.GetAsync(request.Scope, request.WaveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");
        if (wave.Status is not (WaveStatus.Approved or WaveStatus.Frozen))
        {
            throw new MappingUploadPreconditionException(
                "A onda-fonte não está em um estado imutável (Approved/Frozen); validação não permitida.");
        }

        // (§13) Decodificação UTF-8 ESTRITA SEM BOM. Falha ⇒ tentativa Rejected (conteúdo recebido + hash,
        // porém não validável semanticamente) — ainda assim custodiada.
        MappingValidationAttemptOutcome outcome;
        int? rowCount;
        int issueCount;
        bool truncated;
        IReadOnlyList<MappingValidationIssue> issues;

        if (!TryDecodeStrictUtf8(bytes, out var text, out var encodingIssue))
        {
            outcome = MappingValidationAttemptOutcome.Rejected;
            rowCount = null;
            issues = [encodingIssue!];
            issueCount = 1;
            truncated = false;
        }
        else
        {
            var validation = MappingCsvValidator.ValidateUpload(
                text, wave, request.ContentCodePage, policy, _limits.MaxPersistedValidationIssues);
            outcome = validation.IsValid ? MappingValidationAttemptOutcome.Valid : MappingValidationAttemptOutcome.Invalid;
            rowCount = validation.RowCount;
            issues = validation.Issues;
            issueCount = validation.IssueCount;
            truncated = validation.IssuesTruncated;
        }

        // (§23/§34) Custódia durável idempotente. O snapshot da onda (versão/hashes) é revalidado no store.
        var attempt = new MappingValidationAttempt(
            Guid.NewGuid(),
            request.Scope,
            request.WaveId,
            wave.Version.Value,
            wave.ConfigurationHash,
            wave.SelectionHash,
            MappingSchema.Version,
            policy.Version,
            request.ContentCodePage,
            contentSha,
            sizeBytes,
            rowCount,
            outcome,
            issueCount,
            truncated,
            displayName,
            request.UserId,
            requestedBy,
            request.Correlation,
            request.IdempotencyKey,
            _clock.UtcNow,
            issues);

        var persisted = await _store.PersistAsync(attempt, cancellationToken).ConfigureAwait(false);

        // (§3) A resposta reflete SEMPRE a evidência CANÔNICA persistida (a tentativa original em caso de
        // replay), nunca o resultado recalculado nesta execução. Em criação, a tentativa canônica é a de
        // entrada (mesmos bytes/hash/desfecho/problemas), então a semântica é idêntica.
        var canonical = persisted.Attempt;
        return new MappingUploadValidationOutcome(
            canonical.ValidationId,
            persisted.Created,
            persisted.Replayed,
            canonical.Outcome == MappingValidationAttemptOutcome.Valid,
            canonical.Outcome,
            canonical.ContentSha256,
            canonical.SizeBytes,
            canonical.RowCount,
            canonical.IssueCount,
            canonical.IssuesTruncated,
            canonical.Issues);
    }

    // (§6) Normaliza e valida o RequestedBy (metadado operacional de identidade, não custódia de conteúdo):
    // obrigatório, Trim(), não vazio, no máximo 200 caracteres e sem caracteres de controle. Entrada inválida
    // ⇒ ArgumentException (fail-closed, antes de qualquer escrita). Persiste-se o valor normalizado.
    private static string NormalizeRequestedBy(string? requestedBy)
    {
        var trimmed = (requestedBy ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxRequestedByLength)
        {
            throw new ArgumentException(
                $"RequestedBy é obrigatório e deve ter entre 1 e {MaxRequestedByLength} caracteres.", nameof(requestedBy));
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("RequestedBy não pode conter caracteres de controle.", nameof(requestedBy));
            }
        }

        return trimmed;
    }

    // Normaliza SOMENTE o basename para metadado de EXIBIÇÃO. Remove componentes de caminho (defesa contra
    // traversal — '/' e '\' em qualquer plataforma), valida extensão .csv (case-insensitive), rejeita nome
    // vazio/longo demais/com caracteres de controle. Nunca é usado como caminho físico nem chave de autorização.
    private static string NormalizeDisplayFileName(string fileName)
    {
        var raw = fileName ?? string.Empty;
        var lastSeparator = raw.LastIndexOfAny(['/', '\\']);
        var basename = (lastSeparator >= 0 ? raw[(lastSeparator + 1)..] : raw).Trim();

        if (basename.Length is 0 or > MaxDisplayFileNameLength)
        {
            throw new MappingUploadRejectedException(MappingUploadRejectionReason.InvalidFileName);
        }

        foreach (var character in basename)
        {
            if (char.IsControl(character))
            {
                throw new MappingUploadRejectedException(MappingUploadRejectionReason.InvalidFileName);
            }
        }

        if (!basename.EndsWith(CsvExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new MappingUploadRejectedException(MappingUploadRejectionReason.InvalidExtension);
        }

        return basename;
    }

    // Lê no MÁXIMO limit + 1 bytes: se o (limit + 1)-ésimo byte existir, o conteúdo é grande demais e a
    // leitura para imediatamente (nunca consome um stream arbitrariamente grande). Devolve os bytes exatos
    // quando <= limit; devolve null quando excede.
    private static async Task<byte[]?> ReadBoundedAsync(Stream content, long limit, CancellationToken cancellationToken)
    {
        var ceiling = limit + 1; // 1 byte além do limite basta para decidir "grande demais".
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        while (total < ceiling)
        {
            var toRead = (int)Math.Min(chunk.Length, ceiling - total);
            var read = await content.ReadAsync(chunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            buffer.Write(chunk, 0, read);
        }

        return total > limit ? null : buffer.ToArray();
    }

    // UTF-8 ESTRITO e SEM BOM. Qualquer BOM (UTF-8/16/32) e qualquer sequência UTF-8 inválida são rejeitados
    // — sem auto-detecção, sem fallback 1252, sem "correção" de bytes. ContentCodePage do CSV é CONTEÚDO do
    // mapping, não a codificação FÍSICA do arquivo (que permanece UTF-8 sem BOM).
    private static bool TryDecodeStrictUtf8(byte[] bytes, out string text, out MappingValidationIssue? encodingIssue)
    {
        text = string.Empty;
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            encodingIssue = Encoding(MappingValidationIssueCode.Bom, "Arquivo contém BOM; esperado UTF-8 sem BOM.");
            return false;
        }

        if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF) || StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00)
            || StartsWith(bytes, 0xFE, 0xFF) || StartsWith(bytes, 0xFF, 0xFE))
        {
            encodingIssue = Encoding(MappingValidationIssueCode.InvalidEncoding, "Codificação não é UTF-8 sem BOM.");
            return false;
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strict.GetString(bytes);
            encodingIssue = null;
            return true;
        }
        catch (DecoderFallbackException)
        {
            encodingIssue = Encoding(MappingValidationIssueCode.InvalidEncoding, "Bytes não decodificáveis como UTF-8 estrito.");
            return false;
        }
    }

    private static MappingValidationIssue Encoding(string code, string message) => new(code, null, null, message);

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
