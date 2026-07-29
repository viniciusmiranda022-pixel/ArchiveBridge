using ArchiveBridge.Domain.Reconciliation;

namespace ArchiveBridge.Contracts.Reconciliation;

/// <summary>
/// Porta de reconciliação esperado × observado (runbook §26.3). Fail-closed: um resultado
/// <see cref="ReconciliationOutcome.Inconclusive"/> não conta como sucesso.
/// </summary>
public interface IReconciliationService
{
    /// <summary>Avalia contagens esperadas contra observadas e devolve o desfecho de reconciliação.</summary>
    Task<ReconciliationOutcome> EvaluateAsync(long expected, long observed, CancellationToken cancellationToken);
}
