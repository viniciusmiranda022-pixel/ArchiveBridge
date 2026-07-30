using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2ImmutabilityTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task ApprovedWaveTargetChangeIsBlockedByTrigger()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var store = Slice2Support.WaveStore(fixture);
        await store.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await store.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);

        // Tentativa direta (fora do domínio) de mudar o destino de uma onda aprovada: o gatilho barra.
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "UPDATE dbo.migration_waves SET target_root_folder = @f WHERE wave_id = @w;",
            connection.Connection);
        command.Parameters.AddWithValue("@f", "/ImportedPst_Swapped_X");
        command.Parameters.AddWithValue("@w", wave.Id.Value);

        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TargetRootFolderIsGloballyUniqueAcrossProjects()
    {
        var tenant = new Domain.IdentityAndAccess.TenantId(Guid.NewGuid());
        var scopeP1 = new TenantScope(tenant, new Domain.Projects.ProjectId(Guid.NewGuid()));
        var scopeP2 = new TenantScope(tenant, new Domain.Projects.ProjectId(Guid.NewGuid()));
        var projects = Slice2Support.ProjectStore(fixture);
        await projects.AddAsync(Slice2Support.NewProject(scopeP1), CorrelationId.New(), CancellationToken.None);
        await projects.AddAsync(Slice2Support.NewProject(scopeP2), CorrelationId.New(), CancellationToken.None);

        var folder = Slice2Support.UniqueFolder();
        var waves = Slice2Support.WaveStore(fixture);
        var wave1 = Slice2Support.NewWave(scopeP1, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]), folder);
        await waves.AddAsync(wave1, CorrelationId.New(), CancellationToken.None);

        var wave2 = Slice2Support.NewWave(scopeP2, new WaveSelection([Slice2Support.Entry("b.pst", "v@contoso.com", 10)]), folder);
        await Assert.ThrowsAsync<SqlException>(() =>
            waves.AddAsync(wave2, CorrelationId.New(), CancellationToken.None));
    }
}
