namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Qual das duas listas filhas de UMA avaliação de reconciliação (AB-I6-007 Passo 3) contém o item sendo
/// disposto (AB-I6-010) — junto de <c>ItemKey</c> (o valor opaco de <see cref="PstReconciliationItem.RemoteName"/>
/// ou <see cref="ArchiveReconciliationItem.Archive"/>), forma a identidade completa de UMA exceção de
/// reconciliação. O caller nunca fornece mais do que este par opaco (item 2 do work order) — a resolução do
/// item técnico real (disposition, contadores, deltas) é sempre feita server-side a partir da avaliação
/// canônica vigente.
/// </summary>
public enum ReconciliationExceptionItemKind
{
    /// <summary>O item pertence à lista de <see cref="PstReconciliationItem"/> da avaliação.</summary>
    Pst,

    /// <summary>O item pertence à lista de <see cref="ArchiveReconciliationItem"/> da avaliação.</summary>
    Archive,
}
