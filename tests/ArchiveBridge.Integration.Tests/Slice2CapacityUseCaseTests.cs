using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2CapacityUseCaseTests(SqlServerFixture fixture)
{
    private const long Limit = CapacityRule.HundredGigabytesInBytes;

    private ValidateWaveUseCase UseCase(MutableClock clock) =>
        new(Slice2Support.WaveStore(fixture), Slice2Support.PlanningStore(fixture), fixture.Store(clock), clock);

    private async Task<(TenantScope Scope, WaveId WaveId)> SeedWaveAsync(WaveSelection selection)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, selection);
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    [Fact]
    public async Task WaveOverHundredGigabytesIsBlockedWithAssessmentRecorded()
    {
        var (scope, waveId) = await SeedWaveAsync(
            new WaveSelection([Slice2Support.Entry("a.pst", "shared@contoso.com", Limit + 1)]));
        var result = await UseCase(new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, waveId, CorrelationId.New(), CancellationToken.None);

        Assert.True(result.AssessmentRequired);
        Assert.Equal(WaveStatus.Blocked, result.Status);

        var (ruleCode, resultCode) = await ReadAssessmentAsync(scope, waveId);
        Assert.Equal("MICROSOFT_ASSESSMENT_REQUIRED", ruleCode);
        Assert.Equal((int)CapacityAssessmentResult.AssessmentRequired, resultCode);
    }

    [Fact]
    public async Task WaveWithinLimitBecomesReadyForApproval()
    {
        var (scope, waveId) = await SeedWaveAsync(
            new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", Limit)]));
        var result = await UseCase(new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, waveId, CorrelationId.New(), CancellationToken.None);

        Assert.False(result.AssessmentRequired);
        Assert.Equal(WaveStatus.ReadyForApproval, result.Status);
    }

    private async Task<(string RuleCode, int Result)> ReadAssessmentAsync(TenantScope scope, WaveId waveId)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT TOP 1 rule_code, result FROM dbo.planning_assessments WHERE wave_id = @w AND project_id = @p ORDER BY assessment_id DESC;",
            connection.Connection);
        command.Parameters.AddWithValue("@w", waveId.Value);
        command.Parameters.AddWithValue("@p", scope.Project.Value);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        await reader.ReadAsync(CancellationToken.None);
        return (reader.GetString(0), reader.GetByte(1));
    }
}
