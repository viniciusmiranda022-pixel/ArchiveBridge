using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// UMA entrada do resumo estruturado de desvios de um certificate (AB-I6-013 item 10) — participa
/// EXCLUSIVAMENTE de <see cref="ReconciliationCertificateDeviationsHash.Compute"/> (nunca persistida como
/// linha própria do certificate: os itens de origem já são persistidos/auditáveis nas tabelas do Passo 3/4;
/// o certificate referencia-os por identificador opaco e código de desvio, nunca duplica conteúdo/PII —
/// item 11). <see cref="ItemKey"/> é o mesmo identificador opaco (<see cref="PstReconciliationItem.RemoteName"/>/
/// <see cref="ArchiveReconciliationItem.Archive"/>) já usado pelo workflow de disposition (Passo 4).
/// </summary>
public sealed record ReconciliationCertificateDeviationEntry(
    ReconciliationExceptionItemKind ItemKind,
    string ItemKey,
    ReconciliationDisposition TechnicalDisposition,
    ReconciliationCertificateDeviationCode Code);

/// <summary>
/// Hash agregado, determinístico e ORDEM-INDEPENDENTE do resumo estruturado de desvios de um certificate
/// (mesmo padrão de <see cref="ReconciliationPstItemsHash"/>/<see cref="ReconciliationArchiveItemsHash"/>):
/// ordena por <see cref="ReconciliationCertificateDeviationEntry.ItemKind"/>/<see cref="ReconciliationCertificateDeviationEntry.ItemKey"/>
/// (ordinal) ANTES de hashear — duas listas semanticamente equivalentes em ordem diferente produzem o MESMO
/// digest (AB-I6-013 item 17 dos testes obrigatórios).
/// </summary>
public static class ReconciliationCertificateDeviationsHash
{
    /// <summary>Computa o hash agregado a partir das entradas de desvio (lista vazia é um valor válido — zero desvios).</summary>
    public static Sha256Hash Compute(IReadOnlyList<ReconciliationCertificateDeviationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var parts = new List<string>
        {
            "archivebridge.purview.reconciliation-certificate-deviations.v1",
            entries.Count.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var entry in entries
            .OrderBy(e => (int)e.ItemKind)
            .ThenBy(e => e.ItemKey, StringComparer.Ordinal))
        {
            parts.Add(((int)entry.ItemKind).ToString(CultureInfo.InvariantCulture));
            parts.Add(entry.ItemKey);
            parts.Add(((int)entry.TechnicalDisposition).ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)entry.Code).ToString(CultureInfo.InvariantCulture));
        }

        return DeterministicHash.Compute(parts);
    }
}
