using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Contracts.Tests;

public sealed class TenantScopeTests
{
    [Fact]
    public void ValidScopeExposesTenantAndProject()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());

        var scope = new TenantScope(tenant, project);

        Assert.Equal(tenant, scope.Tenant);
        Assert.Equal(project, scope.Project);
    }

    [Fact]
    public void EmptyTenantIsRejectedFailClosed()
    {
        Assert.Throws<ArgumentException>(
            () => new TenantScope(new TenantId(Guid.Empty), new ProjectId(Guid.NewGuid())));
    }
}
