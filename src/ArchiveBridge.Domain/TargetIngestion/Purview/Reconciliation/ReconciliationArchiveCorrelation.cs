using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Correlaciona, de forma PURA e determinística, os snapshots <c>BeforeImport</c>/<c>AfterImport</c>
/// canônicos mais recentes de UM archive (Passo 2) contra a identidade esperada do archive resolvida
/// server-side (AB-I6-007 itens 8-9). Nunca compara snapshots de escopos/archives/fases diferentes (item
/// 8): um snapshot cuja identidade/fase realmente carregada diverge da esperada é bloqueado explicitamente
/// (<see cref="ReconciliationDisposition.BlockedIntegrity"/>) em vez de comparado como se fosse válido —
/// defesa em profundidade além do filtro exato já aplicado por <c>IExoArchiveStatisticsStore.GetLatestAsync</c>.
/// </summary>
public static class ReconciliationArchiveCorrelation
{
    /// <summary>
    /// Correlaciona os snapshots mais recentes (podem ser <see langword="null"/> quando ainda não
    /// capturados) contra a identidade esperada do archive.
    /// </summary>
    public static ArchiveReconciliationItem Correlate(
        TargetArchiveId expectedArchive, ExoArchiveStatisticsSnapshot? before, ExoArchiveStatisticsSnapshot? after)
    {
        if (IsCrossScope(before, expectedArchive, ExoStatisticsPhase.BeforeImport)
            || IsCrossScope(after, expectedArchive, ExoStatisticsPhase.AfterImport))
        {
            return new ArchiveReconciliationItem(
                expectedArchive, ReconciliationDisposition.BlockedIntegrity, before is not null, after is not null,
                ItemCountDelta: null, TotalItemSizeBytesDelta: null);
        }

        if (before is null || after is null)
        {
            // AfterImport sem BeforeImport (ou vice-versa) não produz delta histórico; continua sendo
            // observação válida, porém incompleta para comparações que dependem de baseline (Regras
            // mínimas de avaliação do work order).
            return new ArchiveReconciliationItem(
                expectedArchive, ReconciliationDisposition.IncompleteEvidence, before is not null, after is not null,
                ItemCountDelta: null, TotalItemSizeBytesDelta: null);
        }

        // Deltas calculados SOMENTE quando ambos os lados da métrica são conhecidos (item 9) — nunca por
        // métrica isolada fabricada.
        long? itemCountDelta = before.ItemCount is { } beforeItems && after.ItemCount is { } afterItems
            ? afterItems - beforeItems
            : null;
        long? sizeDelta = before.TotalItemSizeBytes is { } beforeSize && after.TotalItemSizeBytes is { } afterSize
            ? afterSize - beforeSize
            : null;

        var disposition = HasConcreteDecrease(itemCountDelta, sizeDelta)
            ? ReconciliationDisposition.Mismatch
            : itemCountDelta is null && sizeDelta is null
                ? ReconciliationDisposition.IncompleteEvidence
                : ReconciliationDisposition.MatchedWithinEvidence;

        return new ArchiveReconciliationItem(expectedArchive, disposition, BeforeCaptured: true, AfterCaptured: true, itemCountDelta, sizeDelta);
    }

    private static bool HasConcreteDecrease(long? itemCountDelta, long? sizeDelta) =>
        itemCountDelta is < 0 || sizeDelta is < 0;

    // Nunca compara snapshots de escopos, archives ou phases diferentes (item 8) — mesmo que o store já
    // filtre exatamente por archive/fase, esta é uma segunda linha de defesa independente da implementação
    // de store.
    private static bool IsCrossScope(ExoArchiveStatisticsSnapshot? snapshot, TargetArchiveId expectedArchive, ExoStatisticsPhase expectedPhase) =>
        snapshot is not null && (!snapshot.Archive.Equals(expectedArchive) || snapshot.Phase != expectedPhase);
}
