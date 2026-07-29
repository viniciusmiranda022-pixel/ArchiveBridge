using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class HeartbeatReaperRaceTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HeartbeatOnAnExpiredLeaseIsRejected()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var worker = new WorkerId("w");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        // Lease vencido: um heartbeat tardio NÃO pode ressuscitá-lo.
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var outcome = await leases.RenewAsync(
            new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(JobCommandOutcome.FencedOut, outcome);
    }

    [Fact]
    public async Task ReaperDoesNotRecoverALeaseThatWasRenewedBeforeExpiry()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var worker = new WorkerId("w");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Evidence, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Evidence, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        // Renova ANTES da expiração (estende o lease para o futuro).
        clock.Advance(TimeSpan.FromSeconds(20));
        var renew = await leases.RenewAsync(
            new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, renew);

        // Passa da expiração ORIGINAL, mas antes da renovada: o reaper não pode recuperar este Job.
        clock.Advance(TimeSpan.FromSeconds(15)); // 35s do claim; lease renovado expira em 50s
        _ = await leases.RecoverExpiredLeasesAsync(50, CancellationToken.None);

        var job = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Processing, job!.State);
        Assert.Equal(1, job.LeaseEpoch.Value);
        Assert.NotNull(job.LeaseExpiresAtUtc);
    }
}
