using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Waves;

/// <summary>
/// Um artefato selecionado na onda (um PST a importar). Contém apenas metadados de planejamento —
/// nunca conteúdo de e-mail. Caminho e nome são validados (sem traversal; nome termina em .pst).
/// </summary>
public sealed record WaveEntry
{
    private const int MaxPathLength = 400;
    private const int MaxNameLength = 260;

    /// <summary>Cria uma entrada de onda válida.</summary>
    public WaveEntry(string filePath, string pstName, ArchiveRef archive, long sizeBytes, long itemCount)
    {
        FilePath = ValidateFilePath(filePath);
        PstName = ValidatePstName(pstName);
        Archive = archive;
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "sizeBytes não pode ser negativo.");
        }

        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "itemCount não pode ser negativo.");
        }

        SizeBytes = sizeBytes;
        ItemCount = itemCount;
    }

    /// <summary>Caminho de origem do PST (metadado; não é conteúdo).</summary>
    public string FilePath { get; }

    /// <summary>Nome do arquivo PST (termina em .pst; case-sensitive).</summary>
    public string PstName { get; }

    /// <summary>Archive de destino (mailbox).</summary>
    public ArchiveRef Archive { get; }

    /// <summary>Tamanho planejado em bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>Contagem planejada de itens.</summary>
    public long ItemCount { get; }

    private static string ValidateFilePath(string filePath)
    {
        var normalized = TextValue.Require(filePath, nameof(filePath), MaxPathLength);
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("FilePath não pode conter '..' (path traversal).", nameof(filePath));
        }

        return normalized;
    }

    private static string ValidatePstName(string pstName)
    {
        var normalized = TextValue.Require(pstName, nameof(pstName), MaxNameLength);
        if (normalized.Contains('/', StringComparison.Ordinal) || normalized.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("O nome do PST não pode conter separadores de caminho.", nameof(pstName));
        }

        if (!normalized.EndsWith(".pst", StringComparison.Ordinal))
        {
            throw new ArgumentException("O nome do PST deve terminar em '.pst' (case-sensitive).", nameof(pstName));
        }

        return normalized;
    }
}

/// <summary>
/// Seleção versionada de uma onda: o conjunto de entradas. Nomes de PST são únicos (case-sensitive).
/// Expõe totais planejados, hash determinístico e o volume por archive (usado na regra de 100 GB).
/// </summary>
public sealed record WaveSelection
{
    /// <summary>Cria uma seleção; nomes de PST duplicados são rejeitados (fail-closed).</summary>
    public WaveSelection(IReadOnlyList<WaveEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.PstName))
            {
                throw new ArgumentException($"Nome de PST duplicado na seleção: {entry.PstName}.", nameof(entries));
            }
        }

        Entries = entries;
    }

    /// <summary>Entradas da seleção.</summary>
    public IReadOnlyList<WaveEntry> Entries { get; }

    /// <summary>Total planejado de bytes.</summary>
    public long TotalBytes => Entries.Sum(entry => entry.SizeBytes);

    /// <summary>Total planejado de itens.</summary>
    public long TotalItems => Entries.Sum(entry => entry.ItemCount);

    /// <summary>Hash determinístico da seleção (entradas ordenadas canonicamente).</summary>
    public Sha256Hash ComputeHash()
    {
        var parts = new List<string> { nameof(WaveSelection) };
        foreach (var entry in Entries
                     .OrderBy(entry => entry.FilePath, StringComparer.Ordinal)
                     .ThenBy(entry => entry.PstName, StringComparer.Ordinal))
        {
            parts.Add(entry.FilePath);
            parts.Add(entry.PstName);
            parts.Add(entry.Archive.Mailbox);
            parts.Add(entry.SizeBytes.ToString(CultureInfo.InvariantCulture));
            parts.Add(entry.ItemCount.ToString(CultureInfo.InvariantCulture));
        }

        return DeterministicHash.Compute(parts);
    }

    /// <summary>Volume total (bytes) por archive de destino — a soma que a regra de 100 GB avalia.</summary>
    public IReadOnlyDictionary<string, long> BytesByArchive()
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            totals.TryGetValue(entry.Archive.Mailbox, out var current);
            totals[entry.Archive.Mailbox] = current + entry.SizeBytes;
        }

        return totals;
    }
}
