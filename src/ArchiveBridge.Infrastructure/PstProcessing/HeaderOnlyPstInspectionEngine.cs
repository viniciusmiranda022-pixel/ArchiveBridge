using System.Security.Cryptography;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Mapping;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// DECISÃO DE ADAPTER (Slice 4B, Passo 1 — ver documentação do slice): inspetor estrutural SOMENTE DE
/// CABEÇALHO, sem dependência de biblioteca de fornecedor. Nenhum ADR aceito até este Passo autoriza uma
/// engine primária de leitura completa de PST — ADR-0004 (Aspose) foi substituído pelo ADR-0013 e saiu do
/// caminho crítico; ADR-0005 (libpff) permanece <c>BLOCKED_PENDING_EVIDENCE</c> (parecer jurídico LGPL
/// pendente). Consequências explícitas desta escolha, coerentes com "não antecipe capacidades
/// posteriores":
///   - NUNCA percorre a árvore NDB do PST — <c>ItemCount</c>/<c>FolderCount</c> permanecem sempre nulos
///     (ver <see cref="PstInspectionResult"/>); nenhuma contagem é inventada;
///   - classifica ANSI/Unicode/versão apenas pelos 12 bytes iniciais do cabeçalho MS-PST (offsets 0x00
///     dwMagic, 0x08 wMagicClient, 0x0A wVer) — valores públicos e estáveis da especificação [MS-PST],
///     sem invocar nenhum parser de terceiro;
///   - SEMPRE lê e stream-hasheia o arquivo inteiro (mesmo padrão de streaming SHA-256 do runbook §17.2),
///     mesmo quando o cabeçalho já é inválido — o hash observado tem que cobrir os MESMOS bytes que a
///     custódia registrou, para que a comparação de staleness seja sempre significativa;
///   - é substituível: quando uma engine primária real for aceita por ADR futuro, ela implementa
///     <see cref="IPstEngine"/> em paralelo/substituição, sem tocar Domain/Application/Contracts.
/// Abre em modo somente leitura, nunca escreve, nunca segue symlink/reparse point (contenção via
/// <see cref="ArtifactPathContainment"/>) e nunca deixa exceção não tratada escapar como "sucesso" — todo
/// erro de leitura vira <see cref="PstStructuralDiagnostic.ReadError"/> sanitizado (sem stack trace/caminho
/// real). Limite de tamanho/tempo excedido lança <see cref="PstInspectionLimitExceededException"/>.
/// </summary>
public sealed class HeaderOnlyPstInspectionEngine(IPstCustodyStore custodyStore, PstStorageOptions options) : IPstEngine
{
    // MS-PST HEADER (offsets 0x00-0x0B, comuns às variantes ANSI e Unicode):
    //   dwMagic (4 bytes, offset 0)      = 0x21 0x42 0x44 0x4E ("!BDN")
    //   dwCRCPartial (4 bytes, offset 4) — não verificado por este adapter (exigiria CRC32 próprio)
    //   wMagicClient (2 bytes, offset 8) = 0x53 0x4D ("SM")
    //   wVer (2 bytes, offset 10, little-endian): 14/15 = ANSI 97-2002; 23 = Unicode 2003-2010;
    //     36/37 = Unicode 4K (2013+)
    private const int HeaderPrefixLength = 12;
    private const int StreamBufferSize = 4 * 1024 * 1024;
    private static readonly byte[] ExpectedMagic = [0x21, 0x42, 0x44, 0x4E];
    private static readonly byte[] ExpectedClientMagic = [0x53, 0x4D];

    private readonly IPstCustodyStore _custodyStore = custodyStore;
    private readonly PstStorageOptions _options = options;

    /// <inheritdoc />
    public string EngineName => "ArchiveBridge.HeaderOnlyPstInspector";

    /// <inheritdoc />
    public string EngineVersion => "1.0.0";

    /// <inheritdoc />
    public async Task<PstInspectionResult> InspectAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken)
    {
        var custody = await _custodyStore.FindAsync(scope, artifact, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Artefato de custódia não encontrado no momento da inspeção.");

        string absolutePath;
        try
        {
            var candidate = Path.Combine(_options.RootPath, custody.RelativePath.Value);
            absolutePath = ArtifactPathContainment.EnsureContained(_options.RootPath, candidate);
        }
        catch (ArgumentException)
        {
            // Contenção falhou (travessia/symlink) — não deveria ocorrer para um relative_path validado no
            // registro; tratado como leitura sanitizada, nunca stack trace/caminho no diagnóstico.
            return ReadErrorResult();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.Timeout);

        try
        {
            return await InspectFileAsync(absolutePath, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // O token vinculado expirou por CancelAfter (timeout interno do adapter), não pelo chamador —
            // o cancelamento do CHAMADOR deve propagar normalmente (não é capturado aqui).
            throw new PstInspectionLimitExceededException("TIMEOUT");
        }
        catch (IOException)
        {
            return ReadErrorResult();
        }
        catch (UnauthorizedAccessException)
        {
            return ReadErrorResult();
        }
        catch (NotSupportedException)
        {
            return ReadErrorResult();
        }
    }

    private async Task<PstInspectionResult> InspectFileAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(absolutePath);
        if (!fileInfo.Exists)
        {
            return ReadErrorResult();
        }

        if (fileInfo.Length > _options.MaxSizeBytes)
        {
            throw new PstInspectionLimitExceededException("MAX_SIZE_EXCEEDED");
        }

        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var header = new byte[HeaderPrefixLength];
        var headerLength = 0;
        var buffer = new byte[StreamBufferSize];
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalRead = 0;

        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha256.AppendData(buffer.AsSpan(0, read));
            totalRead += read;

            if (headerLength < HeaderPrefixLength)
            {
                var toCopy = Math.Min(HeaderPrefixLength - headerLength, read);
                Buffer.BlockCopy(buffer, 0, header, headerLength, toCopy);
                headerLength += toCopy;
            }
        }

        var observedHash = new Sha256Hash(Convert.ToHexStringLower(sha256.GetHashAndReset()));
        var (diagnostic, variant) = Classify(header, headerLength);
        return new PstInspectionResult(observedHash, totalRead, diagnostic, variant, ItemCount: null, FolderCount: null);
    }

    private static (PstStructuralDiagnostic Diagnostic, PstFormatVariant Variant) Classify(byte[] header, int headerLength)
    {
        if (headerLength < HeaderPrefixLength)
        {
            return (PstStructuralDiagnostic.TooSmall, PstFormatVariant.Unknown);
        }

        if (!header.AsSpan(0, 4).SequenceEqual(ExpectedMagic))
        {
            return (PstStructuralDiagnostic.InvalidSignature, PstFormatVariant.Unknown);
        }

        if (!header.AsSpan(8, 2).SequenceEqual(ExpectedClientMagic))
        {
            return (PstStructuralDiagnostic.InvalidClientSignature, PstFormatVariant.Unknown);
        }

        var version = (ushort)(header[10] | (header[11] << 8)); // wVer, little-endian
        return version switch
        {
            14 or 15 => (PstStructuralDiagnostic.Valid, PstFormatVariant.AnsiLegacy),
            23 => (PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2003To2010),
            36 or 37 => (PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus),
            _ => (PstStructuralDiagnostic.UnsupportedVersion, PstFormatVariant.Unknown),
        };
    }

    private static PstInspectionResult ReadErrorResult() =>
        new(
            ObservedHash: null, ObservedSizeBytes: null, PstStructuralDiagnostic.ReadError,
            PstFormatVariant.Unknown, ItemCount: null, FolderCount: null);
}
