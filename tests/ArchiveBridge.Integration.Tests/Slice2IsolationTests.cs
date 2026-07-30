using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2IsolationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task ProjectOfOneTenantIsNotVisibleToAnother()
    {
        var scopeA = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scopeA), CorrelationId.New(), CancellationToken.None);

        // Outro tenant, mesmo project_id: a RLS por SESSION_CONTEXT esconde a linha (fail-closed).
        var scopeOther = new TenantScope(new TenantId(Guid.NewGuid()), scopeA.Project);
        var loaded = await Slice2Support.ProjectStore(fixture).GetAsync(scopeOther, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task WaveOfOneProjectIsNotVisibleToAnotherProjectSameTenant()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var scopeP1 = new TenantScope(tenant, new ProjectId(Guid.NewGuid()));
        var scopeP2 = new TenantScope(tenant, new ProjectId(Guid.NewGuid()));
        var projects = Slice2Support.ProjectStore(fixture);
        await projects.AddAsync(Slice2Support.NewProject(scopeP1), CorrelationId.New(), CancellationToken.None);
        await projects.AddAsync(Slice2Support.NewProject(scopeP2), CorrelationId.New(), CancellationToken.None);

        var wave = Slice2Support.NewWave(scopeP1, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        // Mesmo tenant, projeto diferente: o filtro explícito por project_id barra o acesso.
        Assert.Null(await Slice2Support.WaveStore(fixture).GetAsync(scopeP2, wave.Id, CancellationToken.None));
        Assert.NotNull(await Slice2Support.WaveStore(fixture).GetAsync(scopeP1, wave.Id, CancellationToken.None));
    }
}
