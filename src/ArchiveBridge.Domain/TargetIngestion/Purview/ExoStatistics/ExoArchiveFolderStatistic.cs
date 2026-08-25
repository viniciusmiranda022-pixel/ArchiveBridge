using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// UMA linha de estatística de pasta do archive EXO (runbook §25.2/§26.2,
/// <c>Get-EXOMailboxFolderStatistics</c>), normalizada e estruturada — nunca uma string formatada/
/// localizada (AB-I6-005 item 8). Identidade estável por <see cref="FolderPath"/> dentro do snapshot
/// (item 9): path/tipo vazio ou oversized e datas temporalmente impossíveis falham fechado no construtor;
/// duplicidade dentro de um conjunto é recusada por <see cref="ExoArchiveFolderStatisticsSet.Canonicalize"/>.
/// Cada contador/data é <see langword="null"/> (Unknown/NotReported) quando o provider não forneceu o
/// campo — NUNCA convertido para zero/data mínima (item 7). Nunca carrega assunto/corpo/remetente/
/// destinatário/anexo — apenas metadado agregado de pasta.
/// </summary>
public sealed record ExoArchiveFolderStatistic
{
    /// <summary>Tamanho máximo de <see cref="FolderPath"/> (bounded, item 9).</summary>
    public const int MaxFolderPathLength = 400;

    /// <summary>Tamanho máximo de <see cref="FolderType"/> (bounded, item 9).</summary>
    public const int MaxFolderTypeLength = 100;

    /// <summary>Cria a linha, validando path/tipo/contadores/ordem temporal (fail-closed).</summary>
    /// <exception cref="ArgumentException"><paramref name="folderPath"/>/<paramref name="folderType"/> vazio, oversized ou com caractere de controle.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Um contador fornecido é negativo.</exception>
    /// <exception cref="ExoArchiveStatisticsValidationException">
    /// <paramref name="oldestItemReceivedDateUtc"/> é posterior a <paramref name="newestItemReceivedDateUtc"/> — data temporalmente impossível.
    /// </exception>
    public ExoArchiveFolderStatistic(
        string folderPath,
        string folderType,
        long? itemsInFolder,
        long? itemsInFolderAndSubfolders,
        long? folderSizeBytes,
        long? folderAndSubfolderSizeBytes,
        DateTimeOffset? oldestItemReceivedDateUtc,
        DateTimeOffset? newestItemReceivedDateUtc)
    {
        FolderPath = TextValue.Require(folderPath, nameof(folderPath), MaxFolderPathLength);
        FolderType = TextValue.Require(folderType, nameof(folderType), MaxFolderTypeLength);
        ItemsInFolder = RequireNonNegativeOrNull(itemsInFolder, nameof(itemsInFolder));
        ItemsInFolderAndSubfolders = RequireNonNegativeOrNull(itemsInFolderAndSubfolders, nameof(itemsInFolderAndSubfolders));
        FolderSizeBytes = RequireNonNegativeOrNull(folderSizeBytes, nameof(folderSizeBytes));
        FolderAndSubfolderSizeBytes = RequireNonNegativeOrNull(folderAndSubfolderSizeBytes, nameof(folderAndSubfolderSizeBytes));

        var canonicalOldest = oldestItemReceivedDateUtc is { } oldest ? TruncateToMilliseconds(oldest) : (DateTimeOffset?)null;
        var canonicalNewest = newestItemReceivedDateUtc is { } newest ? TruncateToMilliseconds(newest) : (DateTimeOffset?)null;
        if (canonicalOldest is { } resolvedOldest && canonicalNewest is { } resolvedNewest && resolvedOldest > resolvedNewest)
        {
            throw new ExoArchiveStatisticsValidationException(
                $"Pasta '{FolderPath}': data temporalmente impossível — OldestItemReceivedDateUtc posterior a NewestItemReceivedDateUtc.");
        }

        OldestItemReceivedDateUtc = canonicalOldest;
        NewestItemReceivedDateUtc = canonicalNewest;
    }

    /// <summary>Caminho da pasta (identidade estável dentro do snapshot).</summary>
    public string FolderPath { get; }

    /// <summary>Tipo/classe distinta da pasta (ex.: Inbox, SentItems, User Created).</summary>
    public string FolderType { get; }

    /// <summary>Itens diretamente na pasta, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? ItemsInFolder { get; }

    /// <summary>Itens na pasta e subpastas, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? ItemsInFolderAndSubfolders { get; }

    /// <summary>Tamanho da pasta em bytes, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? FolderSizeBytes { get; }

    /// <summary>Tamanho da pasta e subpastas em bytes, ou <see langword="null"/> quando não fornecido (Unknown/NotReported).</summary>
    public long? FolderAndSubfolderSizeBytes { get; }

    /// <summary>Data do item mais antigo recebido, ou <see langword="null"/> quando não fornecida (Unknown/NotReported).</summary>
    public DateTimeOffset? OldestItemReceivedDateUtc { get; }

    /// <summary>Data do item mais recente recebido, ou <see langword="null"/> quando não fornecida (Unknown/NotReported).</summary>
    public DateTimeOffset? NewestItemReceivedDateUtc { get; }

    private static long? RequireNonNegativeOrNull(long? value, string parameterName)
    {
        if (value is { } present && present < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, present, "Um contador fornecido não pode ser negativo.");
        }

        return value;
    }

    /// <summary>Trunca para milissegundos (mesma precisão de <c>DATETIME2(3)</c>) para sobreviver ao arredondamento do SQL Server.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }
}
