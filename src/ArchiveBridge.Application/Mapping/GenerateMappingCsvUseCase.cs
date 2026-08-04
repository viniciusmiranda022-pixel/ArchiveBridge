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
/// Resultado de <see cref="GenerateMappingCsvUseCase"/>: o artefato, a evidência de versão persistida
/// e se houve regeneração (falso quando o retry reaproveitou uma versão utilizável idêntica).
/// </summary>
public sealed record MappingGenerationOutcome(MappingDocument Document, MappingCsvVersion Version, bool Regenerated);

/// <summary>
/// Corpo de execução do comando durável <c>GenerateMappingCsv</c>. Gera o CSV a partir de uma onda
/// aprovada/congelada (fail-closed: injeção de fórmula, traversal, limite de 500 linhas, code page
/// fora da política etc. fazem a operação falhar). A versão N+1 é atribuída atomicamente na
/// persistência, sem sobrescrever a anterior (marcada Superseded), e o artefato imutável (mapping.csv
/// + mapping.sha256 + manifesto) é PUBLICADO no armazenamento de artefatos antes do commit — uma
/// versão persistida sempre tem artefato. É idempotente pela IMPRESSÃO DIGITAL COMPLETA de geração
/// (<see cref="MappingGenerationFingerprint"/>): só reaproveita quando TODOS os parâmetros coincidem
/// (configuração, seleção, pasta de destino, code page, esquema, gerador e política) — ao reaproveitar,
/// devolve exatamente o artefato e a evidência daquela versão (o mesmo SHA-256), nunca um documento
/// recém-gerado com evidência antiga.
/// </summary>
public sealed class GenerateMappingCsvUseCase(
    IWaveStore waves, IMappingStore mappings, IMappingArtifactStore artifacts, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly IMappingStore _mappings = mappings;
    private readonly IMappingArtifactStore _artifacts = artifacts;
    private readonly IClock _clock = clock;

    /// <summary>Gera (ou reaproveita) a versão do mapping da onda. Quando <paramref name="fence"/> é informado, o efeito é cercado pelo Job.</summary>
    public async Task<MappingGenerationOutcome> ExecuteAsync(
        TenantScope scope,
        WaveId waveId,
        ContentCodePage contentCodePage,
        MappingPolicy policy,
        string generatedBy,
        CorrelationId correlation,
        CancellationToken cancellationToken,
        JobFence? fence = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = correlation;
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        var result = MappingCsvGenerator.Generate(
            wave, contentCodePage, policy, MappingVersion.Initial, generatedBy, _clock.UtcNow);
        var fingerprint = result.Version.Fingerprint;

        var usable = await _mappings.GetUsableAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
        if (usable is { } current && current.Fingerprint == fingerprint)
        {
            // Reaproveitamento idempotente: devolve o artefato EXATO daquela versão (conjunto validado
            // e bytes conferidos contra o SHA-256 da evidência), nunca o documento recém-gerado.
            var content = await _artifacts
                .GetAsync(new MappingArtifactDescriptor(scope, waveId, current.Version), cancellationToken).ConfigureAwait(false)
                ?? throw new MappingGenerationException(
                    "Versão utilizável sem artefato persistido; evidência inconsistente (fail-closed).");
            var persistedDocument = MappingDocument.FromPersisted(content.Bytes, current.RowCount, current.ContentSha256);
            return new MappingGenerationOutcome(persistedDocument, current, Regenerated: false);
        }

        // Protocolo recuperável, SEM I/O de filesystem sob transação SQL:
        //   1. StageAsync (fora do SQL)  2. ReserveAsync (transação curta 1)  3. PublishAsync (fora do SQL)
        //   4. FinalizeAsync (transação curta 2, valida o bundle publicado ANTES de abrir a transação).
        // Uma reserva PENDENTE da mesma impressão digital (queda entre reservar e finalizar) é recuperada
        // aqui em vez de gerar uma nova versão — a mesma geração republica (idempotente) e finaliza.
        var pending = await _mappings.GetPendingByFingerprintAsync(scope, waveId, fingerprint, cancellationToken).ConfigureAwait(false);

        var staging = await _artifacts
            .StageAsync(result.Document.GetBytes(), result.Document.ContentSha256, cancellationToken).ConfigureAwait(false);
        try
        {
            var reservation = pending
                ?? await _mappings.ReserveAsync(scope, result, staging.SizeBytes, fence, cancellationToken).ConfigureAwait(false);
            var descriptor = new MappingArtifactDescriptor(scope, waveId, reservation.Version);

            // Publica o CONJUNTO imutável FORA de qualquer transação SQL (rename atômico do diretório).
            // Idempotente: se o destino já existe, valida o bundle COMPLETO e recusa divergência (imutável).
            await _artifacts.PublishAsync(staging, descriptor, cancellationToken).ConfigureAwait(false);

            // Finaliza: a validação do bundle publicado roda ANTES de abrir a transação (sem lock durante
            // o I/O de filesystem); em seguida promove PendingArtifact→Usable e substitui a anterior.
            var persisted = await _mappings.FinalizeAsync(
                scope,
                reservation,
                fence,
                async token =>
                {
                    _ = await _artifacts.GetAsync(descriptor, token).ConfigureAwait(false)
                        ?? throw new MappingGenerationException(
                            "Artefato publicado ausente/inválido na finalização (fail-closed).");
                },
                cancellationToken).ConfigureAwait(false);

            return new MappingGenerationOutcome(result.Document, persisted, Regenerated: true);
        }
        catch
        {
            // Rollback do STAGING (nunca de uma versão final): um staging não publicado é descartado.
            // Uma reserva pendente já commitada permanece para reconciliação idempotente numa nova tentativa.
            await _artifacts.DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
