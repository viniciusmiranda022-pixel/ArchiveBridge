namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Desfecho agregado do Production Readiness Review (AB-I8-001). DELIBERADAMENTE possui apenas estes dois
/// valores — não existe, e nunca deve existir, um caso <c>ProductionReady</c>/<c>GoLive</c>/<c>Completed</c>
/// (STOP-THE-LINE do work order: este Passo nunca autoriza aprovação humana final de go-live nem inicia
/// canário). <see cref="NotReady"/> é o default fail-closed (valor 0).
/// </summary>
public enum ProductionReadinessOutcome : byte
{
    /// <summary>Pelo menos um controle obrigatório aplicável não está <see cref="ReadinessControlStatus.Pass"/> — fail-closed default.</summary>
    NotReady = 0,

    /// <summary>
    /// TODOS os controles obrigatórios aplicáveis do catálogo estão <see cref="ReadinessControlStatus.Pass"/>.
    /// Mesmo neste estado, este tipo NUNCA representa aprovação de canário/go-live real — apenas que a
    /// AGREGAÇÃO de evidência já produzida satisfaz os gates do runbook §47; o §48 (canário) e a aprovação
    /// humana final continuam fora do escopo executável deste Passo.
    /// </summary>
    ReadyForCanary = 1,
}
