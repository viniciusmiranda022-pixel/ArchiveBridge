using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.Application.MigrationCompletion;

/// <summary>Lançada quando o ator autenticado não tem papel efetivo autorizado para a ação (AB-I8-010: RBAC sempre server-side, nunca do payload).</summary>
public sealed class MigrationCompletionAuthorizationException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public MigrationCompletionAuthorizationException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public MigrationCompletionAuthorizationException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public MigrationCompletionAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>RBAC server-side de todas as ações do gate de encerramento de migração (AB-I8-010, runbook §49).</summary>
internal static class MigrationCompletionAuthorization
{
    // Compor a avaliação e atestar critérios são ações de escrita de alto impacto (a última linha de defesa
    // antes de considerar uma migração elegível a encerramento) — Administrator/Approver, mesmo par de papéis
    // já usado pelo Passo 1/Passo 2/AuthorizeGoLiveUseCase.
    private static readonly string[] WriteRolesByPrecedence = [PortalRoles.Administrator, PortalRoles.Approver];

    // Leitura é permitida a qualquer papel reconhecido do portal, incluindo Auditor.
    private static readonly string[] ReadRolesByPrecedence =
        [PortalRoles.Administrator, PortalRoles.Approver, PortalRoles.Operator, PortalRoles.Auditor, PortalRoles.Viewer];

    /// <summary>Exige um papel efetivo autorizado a compor uma avaliação ou submeter uma atestação.</summary>
    /// <exception cref="MigrationCompletionAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanWrite(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, WriteRolesByPrecedence);

    /// <summary>Exige um papel efetivo autorizado a ler a avaliação/relatório vigente.</summary>
    /// <exception cref="MigrationCompletionAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanRead(IReadOnlyCollection<string> effectiveRoles) => EnsureCanUse(effectiveRoles, ReadRolesByPrecedence);

    /// <summary>Exige um ator identificado (nunca anônimo).</summary>
    /// <exception cref="MigrationCompletionAuthorizationException"><paramref name="actorId"/> vazio/whitespace.</exception>
    public static string RequireActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new MigrationCompletionAuthorizationException("Ação anônima não é permitida (ator obrigatório).");
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

        throw new MigrationCompletionAuthorizationException("Papel não autorizado para esta ação do gate de encerramento de migração (fail-closed).");
    }
}
