using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.Application.GoLive;

/// <summary>Lançada quando o ator autenticado não tem papel efetivo autorizado para a ação (AB-I8-010: RBAC sempre server-side, nunca do payload).</summary>
public sealed class GoLiveAuthorizationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public GoLiveAuthorizationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public GoLiveAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public GoLiveAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// RBAC server-side de todas as ações de go-live (AB-I8-010, escopo obrigatório item 5: "actor/roles são
/// derivados exclusivamente do contexto autenticado server-side; não aceitar role/actor do payload") — resolve
/// o catálogo concreto de papéis do portal (<see cref="PortalRoles"/>).
/// </summary>
internal static class GoLiveAuthorization
{
    // Autorizar go-live é a decisão de escrita mais sensível deste Passo (habilita a primeira onda real) —
    // mesmo par de papéis Administrator/Approver já usado para autorizar o plano de canário e aprovar a
    // primeira onda do canário (CanaryAuthorization).
    private static readonly string[] AuthorizeRolesByPrecedence = [PortalRoles.Administrator, PortalRoles.Approver];

    // Leitura é permitida a qualquer papel reconhecido do portal, incluindo Auditor.
    private static readonly string[] ReadRolesByPrecedence =
        [PortalRoles.Administrator, PortalRoles.Approver, PortalRoles.Operator, PortalRoles.Auditor, PortalRoles.Viewer];

    /// <summary>Exige um papel efetivo autorizado a autorizar go-live.</summary>
    /// <exception cref="GoLiveAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanAuthorize(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, AuthorizeRolesByPrecedence);

    /// <summary>Exige um papel efetivo autorizado a ler o relatório vigente.</summary>
    /// <exception cref="GoLiveAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanRead(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, ReadRolesByPrecedence);

    /// <summary>Exige um ator identificado (nunca anônimo).</summary>
    /// <exception cref="GoLiveAuthorizationException"><paramref name="actorId"/> vazio/whitespace.</exception>
    public static string RequireActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new GoLiveAuthorizationException("Ação anônima não é permitida (ator obrigatório).");
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

        throw new GoLiveAuthorizationException("Papel não autorizado para esta ação de go-live (fail-closed).");
    }
}
