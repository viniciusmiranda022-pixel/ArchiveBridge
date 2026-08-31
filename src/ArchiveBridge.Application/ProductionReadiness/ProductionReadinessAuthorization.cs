using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>Lançada quando o ator autenticado não tem papel efetivo autorizado para a ação (AB-I8-001 escopo item 9: RBAC sempre server-side, nunca do payload).</summary>
public sealed class ProductionReadinessAuthorizationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public ProductionReadinessAuthorizationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public ProductionReadinessAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public ProductionReadinessAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// RBAC server-side de todas as ações do Production Readiness Review (AB-I8-001 escopo item 9): resolve o
/// catálogo concreto de papéis do portal (<see cref="PortalRoles"/>) — nenhum papel/ator fornecido pelo
/// payload é confiável (mesmo princípio de <see cref="ArchiveBridge.Contracts.Abstractions.IAuthenticatedActorAccessor"/>,
/// AB-I6-012).
/// </summary>
internal static class ProductionReadinessAuthorization
{
    // Compor/atestar são ações de escrita que produzem evidência auditável — nunca uma ação de leitura de
    // rotina; Approver/Administrator, mesmo par de papéis já usado por IssueReconciliationCertificateUseCase.
    private static readonly string[] WriteRolesByPrecedence = [PortalRoles.Administrator, PortalRoles.Approver];

    // Leitura (relatório/último snapshot) é permitida a qualquer papel reconhecido do portal, incluindo
    // Auditor (observabilidade nunca executa ações) — nunca a um ator anônimo/não reconhecido.
    private static readonly string[] ReadRolesByPrecedence =
        [PortalRoles.Administrator, PortalRoles.Approver, PortalRoles.Operator, PortalRoles.Auditor, PortalRoles.Viewer];

    /// <summary>Exige um papel efetivo autorizado a compor um novo snapshot ou submeter uma atestação.</summary>
    /// <exception cref="ProductionReadinessAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanWrite(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, WriteRolesByPrecedence);

    /// <summary>Exige um papel efetivo autorizado a ler o snapshot/relatório vigente.</summary>
    /// <exception cref="ProductionReadinessAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanRead(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, ReadRolesByPrecedence);

    /// <summary>Exige um ator identificado (nunca anônimo).</summary>
    /// <exception cref="ProductionReadinessAuthorizationException"><paramref name="actorId"/> vazio/whitespace.</exception>
    public static string RequireActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ProductionReadinessAuthorizationException("Ação anônima não é permitida (ator obrigatório).");
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

        throw new ProductionReadinessAuthorizationException("Papel não autorizado para esta ação do Production Readiness Review (fail-closed).");
    }
}
