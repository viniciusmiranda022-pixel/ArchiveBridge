using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Hash determinístico do conjunto COMPLETO de itens de archive (mailbox before/after) de uma avaliação de
/// reconciliação (AB-I6-007 item 10) — revalidado a cada leitura, mesmo princípio de
/// <see cref="ReconciliationPstItemsHash"/>.
/// </summary>
public static class ReconciliationArchiveItemsHash
{
    private const string HashPrefix = "archivebridge.purview.reconciliation-archive-items.v1";

    /// <summary>
    /// Calcula o hash a partir dos itens, ordenados deterministicamente por
    /// <see cref="ArchiveReconciliationItem.Archive"/> (Ordinal) — nunca pela ordem de leitura/inserção.
    /// </summary>
    public static Sha256Hash Compute(IReadOnlyList<ArchiveReconciliationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var parts = new List<string> { HashPrefix, items.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var item in items.OrderBy(item => item.Archive.Value, StringComparer.Ordinal))
        {
            parts.Add(item.Archive.Value);
            parts.Add(((int)item.Disposition).ToString(CultureInfo.InvariantCulture));
            parts.Add(item.BeforeCaptured.ToString(CultureInfo.InvariantCulture));
            parts.Add(item.AfterCaptured.ToString(CultureInfo.InvariantCulture));
            parts.Add(item.ItemCountDelta?.ToString(CultureInfo.InvariantCulture) ?? "null");
            parts.Add(item.TotalItemSizeBytesDelta?.ToString(CultureInfo.InvariantCulture) ?? "null");
        }

        return DeterministicHash.Compute(parts);
    }
}
