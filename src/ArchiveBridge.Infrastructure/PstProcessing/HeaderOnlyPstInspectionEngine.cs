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
/// Abre em modo somente leitura, nunca escreve, e nunca deixa exceção não tratada escapar como "sucesso" —
/// todo erro de leitura vira <see cref="PstStructuralDiagnostic.ReadError"/> sanitizado (sem stack
/// trace/caminho real). Limite de tamanho/tempo excedido lança <see cref="PstInspectionLimitExceededException"/>.
///
/// GARANTIA DE SYMLINK/REPARSE (ver threat model do Passo 1 — alegação corrigida em AB-4B-002 item 3):
/// <see cref="ArtifactPathContainment"/> rejeita qualquer symlink/reparse point observado na cadeia de
/// diretórios NO MOMENTO da checagem, e esta engine repete a mesma checagem uma SEGUNDA vez, imediatamente
/// após abrir o <see cref="FileStream"/> e ANTES de ler qualquer byte — estreitando a janela TOCTOU de
/// "check→open" para "check→open→recheck→leitura". Isto NÃO é uma garantia atômica: as duas checagens
/// reexaminam o CAMINHO no sistema de arquivos, não o handle/descritor já aberto; uma verificação
/// baseada no handle (ex.: resolver o destino real do descritor de arquivo via API específica de
/// plataforma) eliminaria a janela por completo, mas exige interop específico de SO sem ADR aceito até
/// este Passo. Qualquer reparse point detectado em qualquer uma das duas checagens falha fechado como
/// <see cref="PstStructuralDiagnostic.ReadError"/> — nunca lê nem hasheia o conteúdo.
///
/// FRONTEIRA DE <see cref="PstStorageOptions.MaxSizeBytes"/> (corrigida em AB-4B-003): o
/// <see cref="FileInfo.Length"/> consultado antes de abrir o stream é APENAS um fast-fail — nunca a
/// fronteira de segurança, porque o arquivo pode crescer/ser substituído entre esse "stat" e a abertura
/// real (janela TOCTOU stat→open). A fronteira de fato é reforçada DUAS vezes sobre o stream já aberto:
/// (1) revalidando <c>stream.Length</c> antes de ler qualquer byte, quando o stream suporta expor um
/// tamanho; e (2), como autoridade FINAL — válida mesmo que a metadata prévia tenha mentido ou o stream não
/// suporte <c>Length</c> — verificando o total efetivamente lido a CADA chunk do loop de leitura, abortando
/// imediatamente com <see cref="PstInspectionLimitExceededException"/> assim que exceder o limite, antes de
/// terminar o hash. Nenhum hash parcial de um artefato acima do limite é jamais observado ou devolvido.
/// </summary>
public sealed class HeaderOnlyPstInspectionEngine : IPstEngine
{
    // MS-PST HEADER (offsets 0x00-0x0B, comuns às variantes ANSI e Unicode):
    //   dwMagic (4 bytes, offset 0)      = 0x21 0x42 0x44 0x4E ("!BDN")
    //   dwCRCPartial (4 bytes, offset 4) — não verificado por este adapter (exigiria CRC32 próprio)
    //   wMagicClient (2 bytes, offset 8) = 0x53 0x4D ("SM")
    //   wVer (2 bytes, offset 10, little-endian): 14/15 = ANSI 97-2002; 23 = Unicode 2003-2010;
    //     36/37 = Unicode 4K (2013+)
    private const int HeaderPrefixLength = 12;

    // internal (não private): reaproveitado por PhysicalPstArtifactStreamFactory como tamanho de buffer da
    // abertura real do FileStream — mantém o MESMO valor usado antes de AB-4B-003, uma única fonte de verdade.
    internal const int StreamBufferSize = 4 * 1024 * 1024;
    private static readonly byte[] ExpectedMagic = [0x21, 0x42, 0x44, 0x4E];
    private static readonly byte[] ExpectedClientMagic = [0x53, 0x4D];

    private readonly IPstCustodyStore _custodyStore;
    private readonly PstStorageOptions _options;
    private readonly IPstArtifactStreamFactory _streamFactory;

    /// <summary>Constrói a engine com a factory física padrão de stream (uso normal em produção/composition root).</summary>
    public HeaderOnlyPstInspectionEngine(IPstCustodyStore custodyStore, PstStorageOptions options)
        : this(custodyStore, options, PhysicalPstArtifactStreamFactory.Instance)
    {
    }

    /// <summary>
    /// Construtor com seam de <see cref="IPstArtifactStreamFactory"/> injetável. Visível apenas dentro do
    /// assembly (mais <c>InternalsVisibleTo</c> para <c>ArchiveBridge.Integration.Tests</c>) — usado SOMENTE
    /// por testes que provam que <see cref="PstStorageOptions.MaxSizeBytes"/> é reforçado sobre o stream
    /// EFETIVAMENTE lido, e não apenas sobre a metadata de <see cref="FileInfo"/> consultada antes da
    /// abertura (AB-4B-003). Nunca usado pelo composition root — a sobrecarga pública acima sempre usa a
    /// factory física real.
    /// </summary>
    internal HeaderOnlyPstInspectionEngine(IPstCustodyStore custodyStore, PstStorageOptions options, IPstArtifactStreamFactory streamFactory)
    {
        _custodyStore = custodyStore;
        _options = options;
        _streamFactory = streamFactory;
    }

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
            // Fast-fail apenas: uma otimização para rejeitar cedo sem sequer abrir o stream. NUNCA é a
            // fronteira de segurança final — ver revalidação sobre o stream efetivamente aberto/lido abaixo
            // (AB-4B-003: a metadata consultada aqui pode estar desatualizada por uma janela stat→open).
            throw new PstInspectionLimitExceededException("MAX_SIZE_EXCEEDED");
        }

        await using var stream = _streamFactory.OpenRead(absolutePath);

        // Segunda checagem de contenção/reparse (ver doc da classe): estreita, mas não elimina, a janela
        // TOCTOU entre a checagem original (antes do FileStream acima) e o início da leitura. Qualquer
        // reparse point detectado agora falha fechado — a stream já aberta é descartada pelo "await using"
        // do escopo do método, nenhum byte é lido/hasheado.
        try
        {
            ArtifactPathContainment.EnsureContained(_options.RootPath, absolutePath);
        }
        catch (ArgumentException)
        {
            return ReadErrorResult();
        }

        // Revalidação do limite sobre o stream JÁ ABERTO (AB-4B-003), quando o stream suporta expor um
        // tamanho: reforça a fronteira antes de ler qualquer byte, cobrindo o caso em que o arquivo cresceu
        // entre o FileInfo.Length acima (fast-fail) e a abertura do FileStream. Streams que não suportam
        // Length (CanSeek == false) simplesmente pulam esta checagem — o loop de leitura abaixo é a
        // autoridade final e nunca depende desta revalidação para fechar a fronteira.
        if (stream.CanSeek)
        {
            long revalidatedLength;
            try
            {
                revalidatedLength = stream.Length;
            }
            catch (NotSupportedException)
            {
                revalidatedLength = -1;
            }

            if (revalidatedLength > _options.MaxSizeBytes)
            {
                throw new PstInspectionLimitExceededException("MAX_SIZE_EXCEEDED");
            }
        }

        var header = new byte[HeaderPrefixLength];
        var headerLength = 0;
        var buffer = new byte[StreamBufferSize];
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalRead = 0;

        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalRead += read;

            // Autoridade FINAL do limite (AB-4B-003): aplicada sobre os bytes REALMENTE lidos deste stream,
            // a cada chunk — nunca confia apenas no FileInfo/Length prévios, que podem estar desatualizados
            // ou (em streams que não suportam Length) simplesmente ausentes. Aborta imediatamente ao exceder,
            // ANTES de terminar o hash — nenhum hash parcial de um artefato acima do limite é observado ou
            // devolvido como resultado.
            if (totalRead > _options.MaxSizeBytes)
            {
                throw new PstInspectionLimitExceededException("MAX_SIZE_EXCEEDED");
            }

            sha256.AppendData(buffer.AsSpan(0, read));

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
