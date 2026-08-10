using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Ambiente Enterprise Vault resolvido SERVER-SIDE: identidade + alvos de site/directory autoritativos
/// (nunca fornecidos pelo cliente). Só é devolvido quando pertence ao tenant/projeto do escopo.
/// </summary>
public sealed record EvEnvironmentRecord(EvEnvironmentId EnvironmentId, string SiteName, string DirectoryServer);

/// <summary>
/// Catálogo de ambientes EV para AUTORIZAÇÃO/RESOLUÇÃO server-side (distinto do read-model visual). Resolve
/// um ambiente estritamente dentro do <see cref="TenantScope"/> autenticado: só retorna quando
/// <c>tenant == scope.Tenant</c> E <c>project == scope.Project</c> E <c>environment_id == environmentId</c>.
/// Ambiente inexistente, cross-tenant ou cross-project é indistinguível externamente (retorna
/// <see langword="null"/>) — sem informação que permita IDOR/enumeração. O site/directory usados no comando
/// vêm daqui (SQL), nunca do navegador.
/// </summary>
public interface IEvEnvironmentCatalog
{
    /// <summary>Resolve o ambiente dentro do escopo; <see langword="null"/> se inexistente/fora do escopo.</summary>
    Task<EvEnvironmentRecord?> GetAsync(
        TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken);
}
