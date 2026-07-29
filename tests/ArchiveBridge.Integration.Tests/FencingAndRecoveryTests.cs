using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class FencingAndRecoveryTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CompleteFromAWorkerThatIsNotTheOwnerIsFencedOut()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, new WorkerId("owner"), Lease, CorrelationId.New()),
            CancellationToken.None);

        var outcome = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, new WorkerId("intruder"), claimed!.Epoch, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(JobCommandOutcome.FencedOut, outcome);
    }

    [Fact]
    public async Task AfterRecoveryAndReclaimTheOldEpochIsFencedOnHeartbeatAndComplete()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var oldWorker = new WorkerId("worker-old");
        var newWorker = new WorkerId("worker-new");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        var first = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, oldWorker, Lease, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(1, first!.Epoch.Value);

        // O worker "cai": o lease expira e o reaper recupera o Job (não o perde).
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var recovered = await leases.RecoverExpiredLeasesAsync(10, CancellationToken.None);
        Assert.True(recovered >= 1);

        var afterRecovery = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.RetryScheduled, afterRecovery!.State);

        // Um novo worker reivindica (época 2). O worker antigo (época 1) deve ser barrado.
        clock.Advance(TimeSpan.FromHours(1));
        var second = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, newWorker, Lease, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(2, second!.Epoch.Value);
        Assert.Equal(2, second.AttemptNumber);

        var staleRenew = await leases.RenewAsync(
            new LeaseCommand(scope, jobId, oldWorker, first.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.FencedOut, staleRenew);

        var staleComplete = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, oldWorker, first.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.FencedOut, staleComplete);

        var freshComplete = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, newWorker, second.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, freshComplete);
    }

    [Fact]
    public async Task RecoveryRequeuesTheJobAndRecordsTheTransition()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var audit = fixture.AuditReader();

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Evidence, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Evidence, new WorkerId("w"), Lease, CorrelationId.New()),
            CancellationToken.None);

        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        await leases.RecoverExpiredLeasesAsync(10, CancellationToken.None);

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Contains(transitions, transition => transition.Reason == ReasonCode.LeaseExpiredRecovered);
    }

    [Fact]
    public async Task RecoveryFailsTheJobWhenAttemptsAreExhausted()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var singleAttempt = new RetryPolicy(1, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        var leases = fixture.LeaseManager(clock, singleAttempt, Lease);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Reconciliation, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Reconciliation, new WorkerId("w"), Lease, CorrelationId.New()),
            CancellationToken.None);

        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        await leases.RecoverExpiredLeasesAsync(10, CancellationToken.None);

        var job = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Failed, job!.State);
    }
}
