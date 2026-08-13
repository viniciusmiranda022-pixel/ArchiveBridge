using System.Security.Claims;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.ControlPlane.Composition;

/// <summary>
/// Nomes das claims do portal (identidade + escopo efetivo). O tenant/projeto do usuário autenticado é a
/// ÚNICA fonte do escopo de dados — nunca um valor fornecido pelo cliente na requisição.
/// </summary>
public static class PortalClaims
{
    /// <summary>Identidade persistida do usuário do portal (GUID), usada para auditoria operacional.</summary>
    public const string UserId = "portal_user_id";

    /// <summary>Nome de exibição do usuário.</summary>
    public const string DisplayName = "display_name";

    /// <summary>Tenant efetivo (GUID) — aplica a RLS por tenant nas leituras.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Projeto padrão efetivo (GUID).</summary>
    public const string ProjectId = "project_id";
}

/// <summary>
/// Deriva o <see cref="TenantScope"/> efetivo a partir do principal autenticado (claims). Fail-closed: se as
/// claims de tenant/projeto estiverem ausentes ou inválidas, lança — nenhuma leitura ocorre sem escopo.
/// </summary>
public interface IPortalScopeAccessor
{
    /// <summary>Escopo de tenant/projeto do usuário autenticado.</summary>
    TenantScope Resolve(ClaimsPrincipal user);
}

/// <inheritdoc />
public sealed class PortalScopeAccessor : IPortalScopeAccessor
{
    /// <inheritdoc />
    public TenantScope Resolve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenant = user.FindFirstValue(PortalClaims.TenantId);
        var project = user.FindFirstValue(PortalClaims.ProjectId);
        if (!Guid.TryParse(tenant, out var tenantId) || !Guid.TryParse(project, out var projectId))
        {
            throw new InvalidOperationException("Principal sem tenant/projeto válidos — escopo indefinido (fail-closed).");
        }

        return new TenantScope(new TenantId(tenantId), new ProjectId(projectId));
    }
}
