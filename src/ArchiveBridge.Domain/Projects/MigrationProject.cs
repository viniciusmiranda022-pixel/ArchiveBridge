using ArchiveBridge.Domain.IdentityAndAccess;

namespace ArchiveBridge.Domain.Projects;

/// <summary>Agregado de projeto de migração (skeleton; invariantes reais virão por slice).</summary>
public sealed class MigrationProject(ProjectId id, TenantId tenant)
{
    public ProjectId Id { get; } = id;
    public TenantId Tenant { get; } = tenant;
}
