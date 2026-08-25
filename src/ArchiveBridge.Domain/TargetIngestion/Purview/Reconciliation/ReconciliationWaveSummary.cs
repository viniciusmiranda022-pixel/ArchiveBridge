namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Read model auditável por wave com contagens EXPLÍCITAS por disposition (AB-I6-007 item 13: "contagens
/// explícitas de matched, mismatch, unknown/incomplete") — derivado deterministicamente dos itens já
/// persistidos/revalidados, nunca persistido de forma redundante. Nunca emite certificate nem fecha a
/// wave/projeto — apenas conta.
/// </summary>
public sealed record ReconciliationWaveSummary(
    int PstMatched,
    int PstMismatch,
    int PstIncomplete,
    int PstBlockedIntegrity,
    int PstExtraInProvider,
    int ArchiveMatched,
    int ArchiveMismatch,
    int ArchiveIncomplete,
    int ArchiveBlockedIntegrity)
{
    /// <summary>Deriva o resumo a partir dos itens de PST e de archive de uma avaliação.</summary>
    public static ReconciliationWaveSummary From(
        IReadOnlyList<PstReconciliationItem> pstItems, IReadOnlyList<ArchiveReconciliationItem> archiveItems)
    {
        ArgumentNullException.ThrowIfNull(pstItems);
        ArgumentNullException.ThrowIfNull(archiveItems);

        return new ReconciliationWaveSummary(
            PstMatched: pstItems.Count(item => item.Disposition == ReconciliationDisposition.MatchedWithinEvidence),
            PstMismatch: pstItems.Count(item => item.Disposition == ReconciliationDisposition.Mismatch),
            PstIncomplete: pstItems.Count(item => item.Disposition == ReconciliationDisposition.IncompleteEvidence),
            PstBlockedIntegrity: pstItems.Count(item => item.Disposition == ReconciliationDisposition.BlockedIntegrity),
            PstExtraInProvider: pstItems.Count(item => item.Disposition == ReconciliationDisposition.ExtraInProvider),
            ArchiveMatched: archiveItems.Count(item => item.Disposition == ReconciliationDisposition.MatchedWithinEvidence),
            ArchiveMismatch: archiveItems.Count(item => item.Disposition == ReconciliationDisposition.Mismatch),
            ArchiveIncomplete: archiveItems.Count(item => item.Disposition == ReconciliationDisposition.IncompleteEvidence),
            ArchiveBlockedIntegrity: archiveItems.Count(item => item.Disposition == ReconciliationDisposition.BlockedIntegrity));
    }
}
