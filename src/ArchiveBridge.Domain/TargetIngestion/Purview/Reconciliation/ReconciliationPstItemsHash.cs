using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Hash determinístico do conjunto COMPLETO de itens de PST de uma avaliação de reconciliação (AB-I6-007
/// item 10) — revalidado a cada leitura (mesmo princípio de <c>PurviewServiceResultRowsHash</c>/
/// <c>ExoArchiveFolderStatisticsHash</c>): adulterar qualquer item (inserir/remover/duplicar/alterar
/// disposition/contador) é detectado fail-closed.
/// </summary>
public static class ReconciliationPstItemsHash
{
    private const string HashPrefix = "archivebridge.purview.reconciliation-pst-items.v1";

    /// <summary>
    /// Calcula o hash a partir dos itens, ordenados deterministicamente por
    /// <see cref="PstReconciliationItem.RemoteName"/> (Ordinal) — nunca pela ordem de leitura/inserção.
    /// </summary>
    public static Sha256Hash Compute(IReadOnlyList<PstReconciliationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var parts = new List<string> { HashPrefix, items.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var item in items.OrderBy(item => item.RemoteName.Value, StringComparer.Ordinal))
        {
            parts.Add(item.RemoteName.Value);
            parts.Add(((int)item.Disposition).ToString(CultureInfo.InvariantCulture));
            parts.Add(item.ObservedStatus.HasValue ? ((int)item.ObservedStatus.Value).ToString(CultureInfo.InvariantCulture) : "null");
            parts.Add(item.ImportedItemCount?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(item.ImportedSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(item.SkippedItemCount?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(item.CorruptedItemCount?.ToString(CultureInfo.InvariantCulture) ?? "null");
        }

        return DeterministicHash.Compute(parts);
    }
}
