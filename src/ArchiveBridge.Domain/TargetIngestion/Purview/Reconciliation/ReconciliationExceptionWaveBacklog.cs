namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// UMA linha do backlog de exceções de uma wave (item 14 do work order AB-I6-010) — o item técnico
/// (<see cref="TechnicalDisposition"/>, nunca alterado por nenhuma decisão) e a decisão vigente sobre ele,
/// se houver (<see cref="CurrentStatus"/> é <see cref="ReconciliationExceptionDecisionStatus.Pending"/>
/// quando nenhuma decisão ainda foi registrada NESTA versão de avaliação).
/// </summary>
public sealed record ReconciliationExceptionBacklogEntry(
    ReconciliationExceptionItemKind ItemKind,
    string ItemKey,
    ReconciliationDisposition TechnicalDisposition,
    bool IsDispositionable,
    ReconciliationExceptionDecisionStatus CurrentStatus,
    ReconciliationExceptionReasonCode? CurrentReasonCode,
    int? CurrentDecisionVersion,
    string? CurrentDecidedBy,
    DateTimeOffset? CurrentDecidedAtUtc);

/// <summary>
/// Read model auditável por wave do workflow de disposition (item 14): backlog completo de exceções da
/// versão de avaliação VIGENTE, com a decisão atual de cada uma e contagens explícitas por estado — derivado
/// deterministicamente dos itens/decisões já persistidos/revalidados, nunca persistido de forma redundante.
/// Nunca calcula/expõe um resultado terminal de projeto (STOP-THE-LINE): apenas conta e lista.
/// </summary>
public sealed record ReconciliationExceptionWaveBacklog(
    int AssessmentVersion,
    int PendingCount,
    int AcceptedExceptionCount,
    int RemediationRequiredCount,
    int RejectedCount,
    int NotDispositionableCount,
    IReadOnlyList<ReconciliationExceptionBacklogEntry> Entries)
{
    /// <summary>
    /// Deriva o backlog a partir dos itens de PST/archive da versão VIGENTE da avaliação (Passo 3) e das
    /// decisões vigentes já resolvidas para essa mesma versão (uma por item, a maior <c>DecisionVersion</c>
    /// — resolução de "vigente" é responsabilidade do chamador/store, nunca recalculada aqui). Itens
    /// <see cref="ReconciliationDisposition.MatchedWithinEvidence"/> NUNCA entram no backlog (item 11 — não
    /// são exceções).
    /// </summary>
    public static ReconciliationExceptionWaveBacklog From(
        int assessmentVersion,
        IReadOnlyList<PstReconciliationItem> pstItems,
        IReadOnlyList<ArchiveReconciliationItem> archiveItems,
        IReadOnlyList<ReconciliationExceptionDecision> currentDecisions)
    {
        ArgumentNullException.ThrowIfNull(pstItems);
        ArgumentNullException.ThrowIfNull(archiveItems);
        ArgumentNullException.ThrowIfNull(currentDecisions);

        var decisionsByKey = new Dictionary<(ReconciliationExceptionItemKind, string), ReconciliationExceptionDecision>();
        foreach (var decision in currentDecisions)
        {
            decisionsByKey[(decision.ItemKind, decision.ItemKey)] = decision;
        }

        var entries = new List<ReconciliationExceptionBacklogEntry>();
        foreach (var item in pstItems)
        {
            if (item.Disposition == ReconciliationDisposition.MatchedWithinEvidence)
            {
                continue;
            }

            entries.Add(BuildEntry(ReconciliationExceptionItemKind.Pst, item.RemoteName.Value, item.Disposition, decisionsByKey));
        }

        foreach (var item in archiveItems)
        {
            if (item.Disposition == ReconciliationDisposition.MatchedWithinEvidence)
            {
                continue;
            }

            entries.Add(BuildEntry(ReconciliationExceptionItemKind.Archive, item.Archive.Value, item.Disposition, decisionsByKey));
        }

        return new ReconciliationExceptionWaveBacklog(
            assessmentVersion,
            PendingCount: entries.Count(entry => entry.IsDispositionable && entry.CurrentStatus == ReconciliationExceptionDecisionStatus.Pending),
            AcceptedExceptionCount: entries.Count(entry => entry.CurrentStatus == ReconciliationExceptionDecisionStatus.AcceptedException),
            RemediationRequiredCount: entries.Count(entry => entry.CurrentStatus == ReconciliationExceptionDecisionStatus.RemediationRequired),
            RejectedCount: entries.Count(entry => entry.CurrentStatus == ReconciliationExceptionDecisionStatus.Rejected),
            NotDispositionableCount: entries.Count(entry => !entry.IsDispositionable),
            Entries: entries);
    }

    private static ReconciliationExceptionBacklogEntry BuildEntry(
        ReconciliationExceptionItemKind kind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        Dictionary<(ReconciliationExceptionItemKind, string), ReconciliationExceptionDecision> decisionsByKey)
    {
        var isDispositionable = technicalDisposition != ReconciliationDisposition.BlockedIntegrity;
        if (decisionsByKey.TryGetValue((kind, itemKey), out var decision))
        {
            return new ReconciliationExceptionBacklogEntry(
                kind, itemKey, technicalDisposition, isDispositionable, decision.Status, decision.ReasonCode,
                decision.DecisionVersion, decision.DecidedBy, decision.DecidedAtUtc);
        }

        return new ReconciliationExceptionBacklogEntry(
            kind, itemKey, technicalDisposition, isDispositionable, ReconciliationExceptionDecisionStatus.Pending,
            CurrentReasonCode: null, CurrentDecisionVersion: null, CurrentDecidedBy: null, CurrentDecidedAtUtc: null);
    }
}
