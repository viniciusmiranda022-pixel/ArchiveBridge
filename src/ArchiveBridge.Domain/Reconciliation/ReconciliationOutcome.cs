namespace ArchiveBridge.Domain.Reconciliation;

/// <summary>Resultado de reconciliação esperado × observado (runbook §26.3). Scaffolding.</summary>
public enum ReconciliationOutcome
{
    Pass,
    PassWithExplainedExceptions,
    Inconclusive,
    Fail,
    DuplicateRisk,
}
