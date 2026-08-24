namespace ArchiveBridge.Domain.TargetIngestion;

/// <summary>
/// Provedor de destino externo por trás de <c>ITargetIngestor</c> (runbook §24, ADR-0006/0007). Hoje só
/// <see cref="Purview"/> tem capability registry/precheck implementados (I5/EPIC-06 Passo 1) — o Graph
/// permanece adapter condicional bloqueado (ADR-0007) e não é modelado aqui até seu próprio Passo.
/// </summary>
public enum TargetProvider
{
    /// <summary>Purview Network Upload — adapter GA inicial planejado para PST (ADR-0006).</summary>
    Purview,
}
