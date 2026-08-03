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

/// <summary>
/// B3: as FKs compostas das tabelas filhas incluem tenant_id/project_id, de modo que uma linha NÃO
/// pode declarar um escopo diferente do pai mesmo conhecendo o (wave_id, wave_version) ou
/// (wave_id, mapping_version). A RLS valida o tenant da própria linha; a coerência com o pai é
/// garantida pela FK composta no banco.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2CompositeFkTests(SqlServerFixture fixture)
{
    private static readonly ContentCodePage CodePage = new(1252);

    private async Task<(TenantScope Scope, WaveId WaveId)> SeedWaveAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    private async Task<(TenantScope Scope, WaveId WaveId)> SeedMappingAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var waveStore = Slice2Support.WaveStore(fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        var result = MappingCsvGenerator.Generate(wave, CodePage, MappingPolicy.Default, MappingVersion.Initial, "do", Slice2Support.Now);
        await Slice2Support.SaveMapping(fixture, scope, result, CancellationToken.None);
        return (scope, wave.Id);
    }

    private async Task<bool> InsertThrowsAsync(TenantScope sessionScope, string sql, Action<SqlCommand> bind)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(sessionScope, CancellationToken.None);
        await using var command = new SqlCommand(sql, connection.Connection);
        bind(command);
        try
        {
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            return false;
        }
        catch (SqlException)
        {
            return true;
        }
    }

    [Fact]
    public async Task WaveEntryWithDivergentTenantIsRejectedByFk()
    {
        var (scopeA, waveId) = await SeedWaveAsync();
        var tenantB = new TenantScope(new TenantId(Guid.NewGuid()), scopeA.Project);
        var threw = await InsertThrowsAsync(tenantB,
            "INSERT INTO dbo.wave_entries (wave_id, tenant_id, project_id, wave_version, file_path, pst_name, mailbox, target_archive_id, size_bytes, item_count) " +
            "VALUES (@w, @t, @p, 1, '/x/z.pst', 'z.pst', 'x@contoso.com', 'X@CONTOSO.COM', 1, 1);",
            c =>
            {
                c.Parameters.AddWithValue("@w", waveId.Value);
                c.Parameters.AddWithValue("@t", tenantB.Tenant.Value);
                c.Parameters.AddWithValue("@p", scopeA.Project.Value);
            });
        Assert.True(threw);
    }

    [Fact]
    public async Task WaveEntryWithDivergentProjectIsRejectedByFk()
    {
        var (scopeA, waveId) = await SeedWaveAsync();
        var otherProject = new ProjectId(Guid.NewGuid());
        var threw = await InsertThrowsAsync(scopeA,
            "INSERT INTO dbo.wave_entries (wave_id, tenant_id, project_id, wave_version, file_path, pst_name, mailbox, target_archive_id, size_bytes, item_count) " +
            "VALUES (@w, @t, @p, 1, '/x/z.pst', 'z.pst', 'x@contoso.com', 'X@CONTOSO.COM', 1, 1);",
            c =>
            {
                c.Parameters.AddWithValue("@w", waveId.Value);
                c.Parameters.AddWithValue("@t", scopeA.Tenant.Value);
                c.Parameters.AddWithValue("@p", otherProject.Value);
            });
        Assert.True(threw);
    }

    [Fact]
    public async Task MappingRowWithDivergentTenantIsRejectedByFk()
    {
        var (scopeA, waveId) = await SeedMappingAsync();
        var tenantB = new TenantScope(new TenantId(Guid.NewGuid()), scopeA.Project);
        var threw = await InsertThrowsAsync(tenantB,
            "INSERT INTO dbo.mapping_csv_rows (wave_id, mapping_version, tenant_id, project_id, row_number, workload, file_path, name, mailbox, is_archive, target_root_folder, content_code_page) " +
            "VALUES (@w, 1, @t, @p, 99, N'Exchange', '/x/z.pst', 'z.pst', 'x@contoso.com', N'TRUE', '/ImportedPst_x_y', 1252);",
            c =>
            {
                c.Parameters.AddWithValue("@w", waveId.Value);
                c.Parameters.AddWithValue("@t", tenantB.Tenant.Value);
                c.Parameters.AddWithValue("@p", scopeA.Project.Value);
            });
        Assert.True(threw);
    }

    [Fact]
    public async Task MappingRowWithDivergentProjectIsRejectedByFk()
    {
        var (scopeA, waveId) = await SeedMappingAsync();
        var otherProject = new ProjectId(Guid.NewGuid());
        var threw = await InsertThrowsAsync(scopeA,
            "INSERT INTO dbo.mapping_csv_rows (wave_id, mapping_version, tenant_id, project_id, row_number, workload, file_path, name, mailbox, is_archive, target_root_folder, content_code_page) " +
            "VALUES (@w, 1, @t, @p, 99, N'Exchange', '/x/z.pst', 'z.pst', 'x@contoso.com', N'TRUE', '/ImportedPst_x_y', 1252);",
            c =>
            {
                c.Parameters.AddWithValue("@w", waveId.Value);
                c.Parameters.AddWithValue("@t", scopeA.Tenant.Value);
                c.Parameters.AddWithValue("@p", otherProject.Value);
            });
        Assert.True(threw);
    }
}
