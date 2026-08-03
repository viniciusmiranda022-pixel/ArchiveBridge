using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2PersistenceTests(SqlServerFixture fixture)
{
    private static readonly ContentCodePage CodePage = new(1252);

    [Fact]
    public async Task ProjectRoundTripsThroughSql()
    {
        var scope = SqlServerFixture.NewScope();
        var project = Slice2Support.NewProject(scope);
        await Slice2Support.ProjectStore(fixture).AddAsync(project, CorrelationId.New(), CancellationToken.None);

        var loaded = await Slice2Support.ProjectStore(fixture).GetAsync(scope, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(project.Id, loaded!.Id);
        Assert.Equal(ProjectStatus.Draft, loaded.Status);
        Assert.Equal(1, loaded.ConfigurationVersion.Value);
        Assert.Equal(project.ConfigurationHash, loaded.ConfigurationHash);
        Assert.Equal("contoso.onmicrosoft.com", loaded.Configuration.TargetTenant.Value);
    }

    [Fact]
    public async Task WaveWithEntriesRoundTripsThroughSql()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var selection = new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10), Slice2Support.Entry("b.pst", "v@contoso.com", 20)]);
        var wave = Slice2Support.NewWave(scope, selection);
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var loaded = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(WaveStatus.Draft, loaded!.Status);
        Assert.Equal(2, loaded.Selection.Entries.Count);
        Assert.Equal(30, loaded.PlannedBytes);
        Assert.Equal(wave.SelectionHash, loaded.SelectionHash);
        Assert.Equal(wave.TargetRootFolder, loaded.TargetRootFolder);
    }

    [Fact]
    public async Task StatusTransitionPersists()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        var store = Slice2Support.WaveStore(fixture);
        await store.AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        Slice2Support.Approve(wave);
        await store.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);

        var loaded = await store.GetAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(WaveStatus.Approved, loaded!.Status);
        Assert.Equal("decision.owner", loaded.ApprovedBy);
    }

    [Fact]
    public async Task MappingVersioningSupersedesPreviousUsableVersion()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var waveStore = Slice2Support.WaveStore(fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);

        var mappingStore = Slice2Support.MappingStore(fixture);
        Assert.Equal(0, await mappingStore.GetMaxVersionAsync(scope, wave.Id, CancellationToken.None));

        var v1 = MappingCsvGenerator.Generate(wave, CodePage, MappingPolicy.Default, MappingVersion.Initial, "do", Slice2Support.Now);
        await Slice2Support.SaveMapping(fixture, scope, v1, CancellationToken.None);
        var v2 = MappingCsvGenerator.Generate(wave, CodePage, MappingPolicy.Default, new MappingVersion(1).Next(), "do", Slice2Support.Now);
        await Slice2Support.SaveMapping(fixture, scope, v2, CancellationToken.None);

        Assert.Equal(2, await mappingStore.GetMaxVersionAsync(scope, wave.Id, CancellationToken.None));

        // v1 = Superseded (1), v2 = Usable (0) — no máximo uma utilizável (índice único filtrado).
        var statuses = await ReadVersionStatusesAsync(scope, wave.Id);
        Assert.Equal(1, statuses[1]);
        Assert.Equal(0, statuses[2]);
    }

    private async Task<Dictionary<int, int>> ReadVersionStatusesAsync(TenantScope scope, WaveId waveId)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT mapping_version, status FROM dbo.mapping_csv_versions WHERE wave_id = @w AND project_id = @p;",
            connection.Connection);
        command.Parameters.AddWithValue("@w", waveId.Value);
        command.Parameters.AddWithValue("@p", scope.Project.Value);
        var map = new Dictionary<int, int>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            map[reader.GetInt32(0)] = reader.GetByte(1);
        }

        return map;
    }
}
