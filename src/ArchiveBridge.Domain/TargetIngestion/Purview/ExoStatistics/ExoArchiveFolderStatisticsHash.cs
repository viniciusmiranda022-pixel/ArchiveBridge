using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Hash determinístico do conjunto COMPLETO de estatísticas de pasta de um snapshot EXO (AB-I6-005 item
/// 11), no mesmo princípio de <c>ServiceResult.PurviewServiceResultRowsHash</c>: participa da evidência
/// persistida e é revalidado a cada leitura — adulterar qualquer pasta (inserir/remover/duplicar/alterar
/// um campo) é detectado fail-closed.
/// </summary>
public static class ExoArchiveFolderStatisticsHash
{
    private const string HashPrefix = "archivebridge.purview.exo-archive-folder-statistics.v1";

    /// <summary>
    /// Calcula o hash a partir das pastas, ordenadas deterministicamente por
    /// <see cref="ExoArchiveFolderStatistic.FolderPath"/> (Ordinal) — nunca pela ordem de leitura/inserção
    /// (critério de aceite 5: mesma observação lógica ⇒ mesmo hash, independentemente de ordem).
    /// </summary>
    public static Sha256Hash Compute(IReadOnlyList<ExoArchiveFolderStatistic> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var parts = new List<string> { HashPrefix, folders.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var folder in folders.OrderBy(folder => folder.FolderPath, StringComparer.Ordinal))
        {
            parts.Add(folder.FolderPath);
            parts.Add(folder.FolderType);
            parts.Add(folder.ItemsInFolder?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(folder.ItemsInFolderAndSubfolders?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(folder.FolderSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(folder.FolderAndSubfolderSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(folder.OldestItemReceivedDateUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(folder.NewestItemReceivedDateUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "null");
        }

        return DeterministicHash.Compute(parts);
    }
}
