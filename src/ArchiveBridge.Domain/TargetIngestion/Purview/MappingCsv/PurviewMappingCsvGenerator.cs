using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;

/// <summary>Documento CSV serializado (bytes UTF-8 sem BOM) e o número de linhas de dados.</summary>
public sealed class PurviewMappingDocument
{
    internal PurviewMappingDocument(string content, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(content);
        // UTF-8 sem BOM: o serviço de importação espera UTF-8 puro.
        Bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        ContentSha256 = DeterministicHash.ComputeBytes(Bytes);
        RowCount = rowCount;
    }

    private PurviewMappingDocument(byte[] bytes, int rowCount, Sha256Hash contentSha256)
    {
        Bytes = bytes;
        RowCount = rowCount;
        ContentSha256 = contentSha256;
    }

    /// <summary>Conteúdo serializado (UTF-8, sem BOM, CRLF).</summary>
    public byte[] Bytes { get; }

    /// <summary>SHA-256 do conteúdo serializado.</summary>
    public Sha256Hash ContentSha256 { get; }

    /// <summary>Número de linhas de dados (sem contar o cabeçalho).</summary>
    public int RowCount { get; }

    /// <summary>
    /// Reconstrói o documento a partir dos bytes persistidos no armazenamento imutável de artefatos,
    /// verificando que o SHA-256 recalculado coincide com a evidência esperada (fail-closed): o
    /// reaproveitamento idempotente NUNCA devolve um documento cujo hash difere da evidência gravada.
    /// </summary>
    public static PurviewMappingDocument FromPersisted(ReadOnlyMemory<byte> bytes, int rowCount, Sha256Hash expectedSha256)
    {
        var copy = bytes.ToArray();
        var actual = DeterministicHash.ComputeBytes(copy);
        if (!string.Equals(actual.Value, expectedSha256.Value, StringComparison.Ordinal))
        {
            throw new PurviewMappingCsvIntegrityViolationException(
                "O SHA-256 do artefato persistido diverge da evidência esperada — reaproveitamento recusado (fail-closed).");
        }

        return new PurviewMappingDocument(copy, rowCount, actual);
    }
}

/// <summary>Resultado completo de uma geração: documento serializado + evidência de versão.</summary>
public sealed record PurviewMappingGenerationResult(PurviewMappingDocument Document, PurviewMappingCsvVersion Evidence);

/// <summary>
/// Serviço de domínio PURO que gera o mapping CSV do Purview a partir de linhas JÁ RESOLVIDAS e
/// VALIDADAS pela Application a partir de evidência canônica de custódia/upload (AB-I5-012/AB-I5-013).
/// Nunca conhece stores, SQL, AzCopy ou qualquer I/O — apenas aplica, de forma fail-closed: limite de 500
/// linhas de dados; nomes de PST únicos no job; e serialização segura contra injeção de fórmula
/// (<see cref="PurviewMappingCsvSerializer"/>). A geração é determinística: a mesma lista de linhas produz
/// sempre o mesmo conteúdo/hash, e a impressão digital (<see cref="PurviewMappingGenerationFingerprint"/>)
/// liga o artefato à evidência exata que o autorizou.
/// </summary>
public static class PurviewMappingCsvGenerator
{
    /// <summary>
    /// Versão do gerador. Faz parte da <see cref="PurviewMappingGenerationFingerprint"/>: uma mudança na
    /// lógica de geração invalida a idempotência de versões produzidas pela lógica anterior.
    /// </summary>
    public const int GeneratorVersion = 1;

    /// <summary>Gera o documento CSV e a evidência de versão a partir de linhas já resolvidas server-side.</summary>
    /// <exception cref="PurviewMappingCsvGenerationException">Nenhuma linha, mais de 500 linhas, ou nome de PST duplicado.</exception>
    public static PurviewMappingGenerationResult Generate(
        WaveId wave,
        ProjectId project,
        TargetRootFolder targetRootFolder,
        IReadOnlyList<PurviewMappingRow> rows,
        Sha256Hash uploadAttemptIdentityHash,
        MappingVersion version,
        string generatedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var author = RequireGeneratedBy(generatedBy);

        if (rows.Count == 0)
        {
            throw new PurviewMappingCsvGenerationException("A onda não tem PSTs verificados; nada a mapear.");
        }

        if (rows.Count > MappingSchema.MaxDataRows)
        {
            throw new PurviewMappingCsvGenerationException(
                $"O mapping excede o limite de {MappingSchema.MaxDataRows} linhas de dados.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!names.Add(row.Name))
            {
                throw new PurviewMappingCsvGenerationException("Nome de PST duplicado no mapping (fail-closed).");
            }
        }

        // Ordem canônica TOTAL e estável, baseada em Name (já comprovado único acima, derivado de
        // ArtifactId+PartSequence — nunca de CreatedAtUtc, que pode empatar entre bindings persistidos em
        // DATETIME2(3) e não tem ordem relativa garantida pelo SQL Server). Canonicaliza ANTES de serializar
        // e ANTES do cálculo do fingerprint: o mesmo conjunto lógico de linhas, entregue em QUALQUER ordem
        // pelo caller, produz sempre os MESMOS bytes/SHA-256/fingerprint (AB-I5-012 acceptance criterion 5).
        var orderedRows = rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToList();

        var content = PurviewMappingCsvSerializer.Serialize(orderedRows);
        var document = new PurviewMappingDocument(content, orderedRows.Count);
        var orderedRowsHash = ComputeOrderedRowsHash(orderedRows);
        var fingerprint = PurviewMappingGenerationFingerprint.Compute(
            wave, targetRootFolder, orderedRowsHash, uploadAttemptIdentityHash, MappingSchema.Version, GeneratorVersion);
        var evidence = new PurviewMappingCsvVersion(
            version,
            project,
            wave,
            document.ContentSha256,
            document.RowCount,
            author,
            now,
            MappingVersionStatus.Usable,
            fingerprint,
            ArtifactPath: string.Empty);

        return new PurviewMappingGenerationResult(document, evidence);
    }

    // Hash agregado do conteúdo lógico de cada linha, na MESMA ordem canônica (por Name, Ordinal) já
    // aplicada pelo chamador antes de invocar este método — nunca reordena por conta própria, para que o
    // fingerprint reflita EXATAMENTE a sequência usada na serialização física (nenhuma ordem "equivalente
    // por acaso").
    private static Sha256Hash ComputeOrderedRowsHash(IReadOnlyList<PurviewMappingRow> rows)
    {
        var parts = new List<string> { nameof(PurviewMappingCsvGenerator) };
        foreach (var row in rows)
        {
            parts.Add(row.FilePath);
            parts.Add(row.Name);
            parts.Add(row.Mailbox);
            parts.Add(row.IsArchive.ToString());
            parts.Add(row.TargetRootFolder.Value);
        }

        return DeterministicHash.Compute(parts);
    }

    private static string RequireGeneratedBy(string generatedBy)
    {
        if (string.IsNullOrWhiteSpace(generatedBy))
        {
            throw new ArgumentException("generatedBy é obrigatório.", nameof(generatedBy));
        }

        return generatedBy.Trim();
    }
}
