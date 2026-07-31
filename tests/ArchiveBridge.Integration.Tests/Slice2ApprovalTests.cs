using ArchiveBridge.Application.Approvals;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>B5: fluxo de aprovações — decisão grava atomicamente estado do agregado + linha em approvals.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2ApprovalTests(SqlServerFixture fixture)
{
    private static ApprovalDecisionCommand Command(TenantScope scope, int version, string by = "decision.owner") =>
        new(scope, version, by, CorrelationId.New(), "sem PII");

    private static MutableClock Clock() => new(Slice2Support.Now);

    private async Task<(TenantScope Scope, WaveId WaveId)> ValidatedWaveAsync(long size)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", size)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await new ValidateWaveUseCase(Slice2Support.WaveStore(fixture), Slice2Support.PlanningStore(fixture), Clock())
            .ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    private async Task<int> ApprovalCountAsync(TenantScope scope, Guid subjectId, int decision)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.approvals WHERE subject_id = @s AND decision = @d AND project_id = @p;",
            connection.Connection);
        command.Parameters.AddWithValue("@s", subjectId);
        command.Parameters.AddWithValue("@d", (byte)decision);
        command.Parameters.AddWithValue("@p", scope.Project.Value);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task ApproveWaveTransitionsAndRecordsDecisionAtomically()
    {
        var (scope, waveId) = await ValidatedWaveAsync(10);
        var result = await new ApproveWaveUseCase(Slice2Support.WaveStore(fixture), Clock())
            .ExecuteAsync(waveId, Command(scope, 1), CancellationToken.None);

        Assert.Equal(WaveStatus.Approved, result.Status);
        Assert.Equal(1, await ApprovalCountAsync(scope, waveId.Value, decision: 0));
    }

    [Fact]
    public async Task ApproveWaveWithStaleVersionIsRejected()
    {
        var (scope, waveId) = await ValidatedWaveAsync(10);
        await Assert.ThrowsAsync<PlanningValidationException>(() =>
            new ApproveWaveUseCase(Slice2Support.WaveStore(fixture), Clock())
                .ExecuteAsync(waveId, Command(scope, 999), CancellationToken.None));
    }

    [Fact]
    public async Task AnonymousApprovalIsRejected()
    {
        var (scope, waveId) = await ValidatedWaveAsync(10);
        await Assert.ThrowsAsync<PlanningValidationException>(() =>
            new ApproveWaveUseCase(Slice2Support.WaveStore(fixture), Clock())
                .ExecuteAsync(waveId, Command(scope, 1, by: "   "), CancellationToken.None));
    }

    [Fact]
    public async Task CannotApproveWaveWithPendingAssessment()
    {
        // Volume acima do limite ⇒ onda Blocked ⇒ aprovar é impossível (validações pendentes).
        var (scope, waveId) = await ValidatedWaveAsync(CapacityRule.OneHundredGigabytesInBytes + 1);
        await Assert.ThrowsAsync<InvalidWaveTransitionException>(() =>
            new ApproveWaveUseCase(Slice2Support.WaveStore(fixture), Clock())
                .ExecuteAsync(waveId, Command(scope, 1), CancellationToken.None));
    }

    [Fact]
    public async Task RejectWaveReturnsToDraftAndRecordsDecision()
    {
        var (scope, waveId) = await ValidatedWaveAsync(10);
        var result = await new RejectWaveUseCase(Slice2Support.WaveStore(fixture), Clock())
            .ExecuteAsync(waveId, Command(scope, 1), CancellationToken.None);

        Assert.Equal(WaveStatus.Draft, result.Status);
        Assert.Equal(1, await ApprovalCountAsync(scope, waveId.Value, decision: 1));
    }

    [Fact]
    public async Task ApproveProjectTransitionsAndRecordsDecision()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        await new ValidateProjectUseCase(Slice2Support.ProjectStore(fixture), Clock())
            .ExecuteAsync(scope, CorrelationId.New(), CancellationToken.None);

        var result = await new ApproveProjectUseCase(Slice2Support.ProjectStore(fixture), Clock())
            .ExecuteAsync(Command(scope, 1), CancellationToken.None);

        Assert.Equal(ProjectStatus.Approved, result.Status);
        Assert.Equal(1, await ApprovalCountAsync(scope, scope.Project.Value, decision: 0));
    }
}
