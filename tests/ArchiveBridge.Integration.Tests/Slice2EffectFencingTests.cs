using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B3: os EFEITOS de uma operação durável são cercados pelo mesmo fencing do Job. Se o worker A perde
/// o lease e o Job é reivindicado por outra época (worker B), a gravação dos efeitos por A é recusada
/// (<see cref="FencedOutException"/>) na MESMA transação — nenhum efeito parcial é persistido.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2EffectFencingTests(SqlServerFixture fixture)
{
    private static readonly WorkerId WorkerA = new("worker-A");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task EffectRejectedWhenJobReclaimedByAnotherEpoch()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var clock = new MutableClock(Slice2Support.Now);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            ArchiveBridge.Application.Planning.PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()), CancellationToken.None);

        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, WorkerA, Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);

        // Simula recuperação pelo reaper + reivindicação por outra época/worker (worker B).
        await ExecuteAsync(scope,
            "UPDATE dbo.jobs SET lease_epoch = lease_epoch + 1, owner_worker = N'worker-B' WHERE job_id = @id;",
            claimed!.Job.JobId.Value);

        var staleFence = new JobFence(scope, claimed.Job.JobId, WorkerA, claimed.Job.Epoch);

        await Assert.ThrowsAsync<FencedOutException>(() =>
            Slice2Support.ValidateWave(fixture, clock).ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None, staleFence));

        // Nenhum efeito do worker A: onda ainda Draft, sem avaliações.
        var reloaded = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(WaveStatus.Draft, reloaded!.Status);
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM dbo.planning_assessments WHERE wave_id = @id;", wave.Id.Value));
    }

    [Fact]
    public async Task EffectAppliedWhenFenceCurrent()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var clock = new MutableClock(Slice2Support.Now);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            ArchiveBridge.Application.Planning.PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()), CancellationToken.None);
        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, WorkerA, Lease, CorrelationId.New(), CancellationToken.None);

        var fence = new JobFence(scope, claimed!.Job.JobId, WorkerA, claimed.Job.Epoch);
        var result = await Slice2Support.ValidateWave(fixture, clock)
            .ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None, fence);

        Assert.Equal(WaveStatus.ReadyForApproval, result.Status);
        Assert.Equal(1, await CountAsync(scope, "SELECT COUNT(*) FROM dbo.planning_assessments WHERE wave_id = @id;", wave.Id.Value));
    }

    private async Task ExecuteAsync(TenantScope scope, string sql, Guid id)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, connection.Connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<int> CountAsync(TenantScope scope, string sql, Guid id)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, connection.Connection);
        command.Parameters.AddWithValue("@id", id);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
