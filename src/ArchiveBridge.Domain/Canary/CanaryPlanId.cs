namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Identidade OPACA de um plano de canário (AB-I8-004, escopo obrigatório item 1: "criar identidade opaca e
/// tenant/project-scoped para um plano de canário"). Mintada UMA vez quando a primeira versão do plano de um
/// (tenant, project) é autorizada e preservada em todas as versões subsequentes do MESMO plano (drift produz
/// uma nova <see cref="CanaryPlan.PlanVersion"/> do plano existente, nunca um <see cref="CanaryPlanId"/> novo)
/// — o identificador nunca é fornecido pelo chamador.
/// </summary>
public readonly record struct CanaryPlanId(Guid Value)
{
    /// <summary>Minta uma nova identidade de plano.</summary>
    public static CanaryPlanId New() => new(Guid.NewGuid());
}
