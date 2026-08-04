using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B5: integridade de VERSÃO no banco. Avaliações e decisões referenciam <c>wave_versions</c> por FK
/// composta (wave_id, wave_version, tenant_id, project_id): uma versão inexistente ou de outro
/// tenant/projeto é recusada NO BANCO, não só no código.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2VersionFkTests(SqlServerFixture fixture)
{
    private async Task<(TenantScope Scope, WaveId WaveId)> SeedWaveAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
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

    private const string InsertAssessment =
        """
        INSERT INTO dbo.planning_assessments
            (tenant_id, project_id, wave_id, wave_version, target_archive_id, total_bytes, rule_code, result, reason, correlation_id)
        VALUES (@t, @p, @w, @v, 'X@CONTOSO.COM', 1, 'WITHIN_LIMIT', 0, 'r', @corr);
        """;

    private const string InsertDecision =
        """
        INSERT INTO dbo.capacity_assessment_decisions
            (tenant_id, project_id, wave_id, wave_version, target_archive_id, decision, evidence_reference, decided_by, correlation_id)
        VALUES (@t, @p, @w, @v, 'X@CONTOSO.COM', 0, 'EV-1', 'do', @corr);
        """;

    [Fact]
    public async Task AssessmentForNonexistentVersionIsRejected()
    {
        var (scope, waveId) = await SeedWaveAsync();
        Assert.True(await InsertThrowsAsync(scope, InsertAssessment, c =>
        {
            c.Parameters.AddWithValue("@t", scope.Tenant.Value);
            c.Parameters.AddWithValue("@p", scope.Project.Value);
            c.Parameters.AddWithValue("@w", waveId.Value);
            c.Parameters.AddWithValue("@v", 999);
            c.Parameters.AddWithValue("@corr", Guid.NewGuid());
        }));
    }

    [Fact]
    public async Task DecisionForNonexistentVersionIsRejected()
    {
        var (scope, waveId) = await SeedWaveAsync();
        Assert.True(await InsertThrowsAsync(scope, InsertDecision, c =>
        {
            c.Parameters.AddWithValue("@t", scope.Tenant.Value);
            c.Parameters.AddWithValue("@p", scope.Project.Value);
            c.Parameters.AddWithValue("@w", waveId.Value);
            c.Parameters.AddWithValue("@v", 999);
            c.Parameters.AddWithValue("@corr", Guid.NewGuid());
        }));
    }

    [Fact]
    public async Task DecisionForAnotherProjectVersionIsRejected()
    {
        var (scope, waveId) = await SeedWaveAsync();
        var otherProject = new ProjectId(Guid.NewGuid());
        Assert.True(await InsertThrowsAsync(scope, InsertDecision, c =>
        {
            c.Parameters.AddWithValue("@t", scope.Tenant.Value);
            c.Parameters.AddWithValue("@p", otherProject.Value); // versão v1 existe para outro projeto? não → FK falha
            c.Parameters.AddWithValue("@w", waveId.Value);
            c.Parameters.AddWithValue("@v", 1);
            c.Parameters.AddWithValue("@corr", Guid.NewGuid());
        }));
    }
}
