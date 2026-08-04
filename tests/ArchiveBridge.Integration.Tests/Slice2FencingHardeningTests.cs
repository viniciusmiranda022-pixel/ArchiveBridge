using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B1: fencing ATÔMICO. A guarda exige lease AINDA VÁLIDO (recusa lease expirado, mesmo não recuperado)
/// e retém o lock da linha do Job (UPDLOCK/HOLDLOCK) até o commit, bloqueando o reaper. O heartbeat
/// (renovação do lease) mantém uma operação longa válida; sua perda impede o efeito.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2FencingHardeningTests(SqlServerFixture fixture)
{
    private static readonly WorkerId WorkerA = new("worker-A");

    private async Task<(TenantScope Scope, MigrationWave Wave, ClaimedJob Job)> ClaimedValidateWaveAsync(MutableClock clock)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()), CancellationToken.None);
        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, WorkerA, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);
        return (scope, wave, claimed!.Job);
    }

    private async Task<int> AssessmentCountAsync(TenantScope scope, Guid waveId)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.planning_assessments WHERE wave_id = @id;", connection.Connection);
        command.Parameters.AddWithValue("@id", waveId);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task ExpiredLeaseDoesNotPersistEffect()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, wave, job) = await ClaimedValidateWaveAsync(clock);

        clock.Advance(TimeSpan.FromMinutes(6)); // além do lease (5 min), mas ainda não recuperado pelo reaper
        var fence = new JobFence(scope, job.JobId, WorkerA, job.Epoch);

        await Assert.ThrowsAsync<FencedOutException>(() =>
            Slice2Support.ValidateWave(fixture, clock).ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None, fence));

        var reloaded = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(WaveStatus.Draft, reloaded!.Status);
        Assert.Equal(0, await AssessmentCountAsync(scope, wave.Id.Value));
    }

    [Fact]
    public async Task HeartbeatKeepsLongOperationValid()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, wave, job) = await ClaimedValidateWaveAsync(clock);

        clock.Advance(TimeSpan.FromMinutes(4)); // operação longa em curso
        var renew = await fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease)
            .RenewAsync(new LeaseCommand(scope, job.JobId, WorkerA, job.Epoch, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, renew); // heartbeat renovou o lease (→ agora+5min)

        clock.Advance(TimeSpan.FromMinutes(2)); // além do lease ORIGINAL, dentro do renovado
        var fence = new JobFence(scope, job.JobId, WorkerA, job.Epoch);
        var result = await Slice2Support.ValidateWave(fixture, clock)
            .ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None, fence);

        Assert.Equal(WaveStatus.ReadyForApproval, result.Status);
        Assert.Equal(1, await AssessmentCountAsync(scope, wave.Id.Value));
    }

    [Fact]
    public async Task LostHeartbeatPreventsEffect()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, wave, job) = await ClaimedValidateWaveAsync(clock);

        clock.Advance(TimeSpan.FromMinutes(7)); // sem heartbeat: lease expira
        var fence = new JobFence(scope, job.JobId, WorkerA, job.Epoch);

        await Assert.ThrowsAsync<FencedOutException>(() =>
            Slice2Support.ValidateWave(fixture, clock).ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None, fence));
        Assert.Equal(0, await AssessmentCountAsync(scope, wave.Id.Value));
    }

    [Fact]
    public async Task EffectLockBlocksReaperUpdate()
    {
        // Prova que a guarda (UPDLOCK/HOLDLOCK) retém o lock da linha do Job até o commit: o UPDATE do
        // reaper (identidade de manutenção) fica bloqueado e estoura LOCK_TIMEOUT enquanto a guarda vive.
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, _, job) = await ClaimedValidateWaveAsync(clock);
        var now = clock.UtcNow.UtcDateTime;

        await using var worker = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var transaction = (SqlTransaction)await worker.Connection.BeginTransactionAsync(CancellationToken.None);
        await using (var guard = new SqlCommand(
            "SELECT 1 FROM dbo.jobs WITH (UPDLOCK, HOLDLOCK) WHERE job_id = @j AND owner_worker = @w AND lease_epoch = @e AND state = 1 AND lease_expires_at_utc > @now;",
            worker.Connection, transaction))
        {
            guard.Parameters.AddWithValue("@j", job.JobId.Value);
            guard.Parameters.AddWithValue("@w", WorkerA.Value);
            guard.Parameters.AddWithValue("@e", job.Epoch.Value);
            guard.Parameters.AddWithValue("@now", now);
            await guard.ExecuteScalarAsync(CancellationToken.None);
        }

        await using var reaper = await fixture.Factory.OpenForMaintenanceAsync(CancellationToken.None);
        await using var reaperUpdate = new SqlCommand(
            "SET LOCK_TIMEOUT 2000; UPDATE dbo.jobs SET lease_epoch = lease_epoch + 1, owner_worker = NULL WHERE job_id = @j;",
            reaper.Connection);
        reaperUpdate.Parameters.AddWithValue("@j", job.JobId.Value);

        var blocked = await Assert.ThrowsAsync<SqlException>(() => reaperUpdate.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.Equal(1222, blocked.Number); // lock request timeout — o reaper foi bloqueado pela guarda

        await transaction.RollbackAsync(CancellationToken.None);
    }
}
