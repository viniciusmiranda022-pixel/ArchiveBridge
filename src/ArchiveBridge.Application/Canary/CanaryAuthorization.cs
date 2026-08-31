using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.Application.Canary;

/// <summary>Lançada quando o ator autenticado não tem papel efetivo autorizado para a ação (AB-I8-004, mesmo princípio de <see cref="ArchiveBridge.Application.ProductionReadiness.ProductionReadinessAuthorizationException"/>: RBAC sempre server-side, nunca do payload).</summary>
public sealed class CanaryAuthorizationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public CanaryAuthorizationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public CanaryAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public CanaryAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// RBAC server-side de todas as ações do canário de produção (AB-I8-004 escopo obrigatório item 2: "actor/
/// roles vêm exclusivamente do contexto autenticado server-side; anti-IDOR obrigatório") — resolve o catálogo
/// concreto de papéis do portal (<see cref="PortalRoles"/>). Nenhum papel/ator fornecido pelo payload é
/// confiável (mesmo princípio de <see cref="ArchiveBridge.Contracts.Abstractions.IAuthenticatedActorAccessor"/>).
/// </summary>
internal static class CanaryAuthorization
{
    // Autorizar um plano é uma ação de escrita de alto impacto (vincula o gate de entrada do canário real) —
    // mesmo par de papéis Administrator/Approver já usado por ComposeProductionReadinessReviewUseCase.
    private static readonly string[] AuthorizePlanRolesByPrecedence = [PortalRoles.Administrator, PortalRoles.Approver];

    // Submeter evidência de cenário (atestação de operador ou resolução SystemDerived) é operação do dia a
    // dia do canário controlado — inclui Operator, que executa os cenários na prática.
    private static readonly string[] SubmitEvidenceRolesByPrecedence =
        [PortalRoles.Administrator, PortalRoles.Approver, PortalRoles.Operator];

    // A aprovação da primeira onda real (escopo obrigatório item 11) é a decisão humana final mais sensível
    // deste Passo — restrita ao mesmo par de papéis usado para autorizar o plano, nunca a Operator.
    private static readonly string[] ApproveFirstWaveRolesByPrecedence = [PortalRoles.Administrator, PortalRoles.Approver];

    // Leitura é permitida a qualquer papel reconhecido do portal, incluindo Auditor (observabilidade nunca
    // executa ações) — nunca a um ator anônimo/não reconhecido.
    private static readonly string[] ReadRolesByPrecedence =
        [PortalRoles.Administrator, PortalRoles.Approver, PortalRoles.Operator, PortalRoles.Auditor, PortalRoles.Viewer];

    /// <summary>Exige um papel efetivo autorizado a autorizar um novo plano de canário.</summary>
    /// <exception cref="CanaryAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanAuthorizePlan(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, AuthorizePlanRolesByPrecedence);

    /// <summary>Exige um papel efetivo autorizado a submeter evidência de cenário.</summary>
    /// <exception cref="CanaryAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanSubmitEvidence(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, SubmitEvidenceRolesByPrecedence);

    /// <summary>Exige um papel efetivo autorizado a aprovar a primeira onda real.</summary>
    /// <exception cref="CanaryAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanApproveFirstWave(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, ApproveFirstWaveRolesByPrecedence);

    /// <summary>Exige um papel efetivo autorizado a ler o plano/relatório vigente.</summary>
    /// <exception cref="CanaryAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanRead(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, ReadRolesByPrecedence);

    /// <summary>Exige um ator identificado (nunca anônimo).</summary>
    /// <exception cref="CanaryAuthorizationException"><paramref name="actorId"/> vazio/whitespace.</exception>
    public static string RequireActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new CanaryAuthorizationException("Ação anônima não é permitida (ator obrigatório).");
        }

        return actorId.Trim();
    }

    private static string EnsureCanUse(IReadOnlyCollection<string> effectiveRoles, IReadOnlyList<string> rolesByPrecedence)
    {
        if (effectiveRoles is { Count: > 0 })
        {
            foreach (var candidate in rolesByPrecedence)
            {
                if (effectiveRoles.Contains(candidate, StringComparer.Ordinal))
                {
                    return candidate;
                }
            }
        }

        throw new CanaryAuthorizationException("Papel não autorizado para esta ação do canário de produção (fail-closed).");
    }
}
