using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>Exceção de validação de manifesto/entrada — fail-closed, nunca aceita dado inconsistente.</summary>
public sealed class EvExportManifestValidationException(string message) : Exception(message);

/// <summary>
/// UMA entrada canônica do manifesto de exportação (AB-4C-005 item 12): identidade (<see cref="Id"/>,
/// SEMPRE derivada do próprio hash de conteúdo — nunca do nome de arquivo, item 10), tamanho e SHA-256
/// calculados APÓS o fechamento do arquivo pelo exporter. <see cref="FileNameHint"/> é preservado apenas
/// como evidência operacional (nunca como identidade, nunca usado para decisão).
/// </summary>
public sealed record EvExportManifestEntry
{
    private const int FileNameHintMaxLength = 260;

    /// <summary>Cria uma entrada de manifesto validada; a identidade é derivada do hash do conteúdo.</summary>
    public EvExportManifestEntry(string fileNameHint, long sizeBytes, Sha256Hash contentSha256)
    {
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Tamanho de output não pode ser negativo.");
        }

        FileNameHint = TextValue.Require(fileNameHint, nameof(fileNameHint), FileNameHintMaxLength);
        SizeBytes = sizeBytes;
        ContentSha256 = contentSha256;
        Id = DeriveId(contentSha256, sizeBytes);
    }

    /// <summary>Identidade opaca derivada exclusivamente do hash+tamanho do conteúdo (nunca do nome de arquivo).</summary>
    public ExportOutputId Id { get; }

    /// <summary>Nome de arquivo observado — evidência operacional, NUNCA identidade.</summary>
    public string FileNameHint { get; }

    /// <summary>Tamanho em bytes, medido após o fechamento do arquivo.</summary>
    public long SizeBytes { get; }

    /// <summary>SHA-256 do conteúdo, calculado após o fechamento do arquivo (streaming).</summary>
    public Sha256Hash ContentSha256 { get; }

    private static ExportOutputId DeriveId(Sha256Hash contentSha256, long sizeBytes)
    {
        var seed = DeterministicHash.Compute(
            [contentSha256.Value, sizeBytes.ToString(CultureInfo.InvariantCulture)]);
        var bytes = Convert.FromHexString(seed.Value);
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new ExportOutputId(new Guid(guidBytes));
    }
}

/// <summary>
/// Manifesto CANÔNICO dos PSTs exportados por UMA tentativa (AB-4C-005 item 12): lineage completa para
/// archive/request/attempt, entradas de output com identidade/tamanho/hash, e a versão do engine/connector
/// que produziu o resultado. O hash do manifesto é determinístico e INDEPENDENTE da ordem de enumeração do
/// filesystem — as entradas são sempre ordenadas por <see cref="EvExportManifestEntry.ContentSha256"/>
/// antes do cálculo, para que dois scans físicos equivalentes produzam sempre o MESMO manifesto.
/// </summary>
public sealed record EvExportManifest
{
    private const int EngineVersionMaxLength = 100;
    private const int ConnectorVersionMaxLength = 50;

    private EvExportManifest(
        ExportRequestId request,
        ExportAttemptId attempt,
        string externalArchiveId,
        IReadOnlyList<EvExportManifestEntry> entries,
        string engineVersion,
        string connectorVersion,
        Sha256Hash manifestHash)
    {
        Request = request;
        Attempt = attempt;
        ExternalArchiveId = externalArchiveId;
        Entries = entries;
        EngineVersion = engineVersion;
        ConnectorVersion = connectorVersion;
        ManifestHash = manifestHash;
    }

    /// <summary>
    /// Constrói o manifesto canônico a partir dos outputs descobertos. Recusa (fail-closed) duas entradas
    /// com a MESMA identidade derivada (mesmo conteúdo/tamanho) — um exporter que produza o mesmo PST
    /// duas vezes no mesmo diretório é uma inconsistência, nunca silenciosamente deduplicada.
    /// </summary>
    public static EvExportManifest Create(
        ExportRequestId request,
        ExportAttemptId attempt,
        string externalArchiveId,
        IReadOnlyList<EvExportManifestEntry> entries,
        string engineVersion,
        string connectorVersion)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sanitizedArchiveId = TextValue.Require(externalArchiveId, nameof(externalArchiveId), 300);
        var sanitizedEngineVersion = TextValue.Require(engineVersion, nameof(engineVersion), EngineVersionMaxLength);
        var sanitizedConnectorVersion = TextValue.Require(connectorVersion, nameof(connectorVersion), ConnectorVersionMaxLength);

        var ordered = entries.OrderBy(entry => entry.ContentSha256.Value, StringComparer.Ordinal).ToArray();
        var seen = new HashSet<ExportOutputId>();
        foreach (var entry in ordered)
        {
            if (!seen.Add(entry.Id))
            {
                throw new EvExportManifestValidationException(
                    "Manifesto de exportação contém outputs com identidade de conteúdo duplicada.");
            }
        }

        var hash = ComputeHash(request, attempt, sanitizedArchiveId, ordered, sanitizedEngineVersion, sanitizedConnectorVersion);
        return new EvExportManifest(
            request, attempt, sanitizedArchiveId, ordered, sanitizedEngineVersion, sanitizedConnectorVersion, hash);
    }

    /// <summary>Reconstrói um manifesto já persistido, revalidando o hash contra os filhos REALMENTE carregados (fail-closed).</summary>
    public static EvExportManifest Rehydrate(
        ExportRequestId request,
        ExportAttemptId attempt,
        string externalArchiveId,
        IReadOnlyList<EvExportManifestEntry> entries,
        string engineVersion,
        string connectorVersion,
        Sha256Hash persistedManifestHash)
    {
        var rebuilt = Create(request, attempt, externalArchiveId, entries, engineVersion, connectorVersion);
        if (!string.Equals(rebuilt.ManifestHash.Value, persistedManifestHash.Value, StringComparison.Ordinal))
        {
            throw new EvExportManifestValidationException(
                "O hash persistido do manifesto não corresponde às entradas realmente carregadas (fail-closed).");
        }

        return rebuilt;
    }

    /// <summary>Pedido de exportação a que este manifesto pertence.</summary>
    public ExportRequestId Request { get; }

    /// <summary>Tentativa que produziu este manifesto.</summary>
    public ExportAttemptId Attempt { get; }

    /// <summary>Identidade externa opaca do archive de origem.</summary>
    public string ExternalArchiveId { get; }

    /// <summary>Entradas do manifesto, em ordem canônica (por hash de conteúdo).</summary>
    public IReadOnlyList<EvExportManifestEntry> Entries { get; }

    /// <summary>Versão observada do engine de exportação (ex.: versão do cmdlet EV).</summary>
    public string EngineVersion { get; }

    /// <summary>Versão do software do connector que executou a exportação.</summary>
    public string ConnectorVersion { get; }

    /// <summary>Hash determinístico do manifesto inteiro.</summary>
    public Sha256Hash ManifestHash { get; }

    private static Sha256Hash ComputeHash(
        ExportRequestId request, ExportAttemptId attempt, string externalArchiveId,
        IReadOnlyList<EvExportManifestEntry> orderedEntries, string engineVersion, string connectorVersion)
    {
        var parts = new List<string>
        {
            request.Value.ToString("N"), attempt.Value.ToString("N"), externalArchiveId, engineVersion, connectorVersion,
        };
        foreach (var entry in orderedEntries)
        {
            parts.Add(entry.ContentSha256.Value);
            parts.Add(entry.SizeBytes.ToString(CultureInfo.InvariantCulture));
        }

        return DeterministicHash.Compute(parts);
    }
}
