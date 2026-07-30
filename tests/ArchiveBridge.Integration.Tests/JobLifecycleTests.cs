using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class JobLifecycleTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CreateClaimRenewCompletePersistsAndAudits()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var audit = fixture.AuditReader();
        var worker = new WorkerId("worker-1");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        var pending = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(JobState.Pending, pending!.State);

        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed!.JobId);
        Assert.Equal(1, claimed.Epoch.Value);
        Assert.Equal(1, claimed.AttemptNumber);

        var renew = await leases.RenewAsync(
            new LeaseCommand(scope, jobId, worker, claimed.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, renew);

        var complete = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, worker, claimed.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, complete);

        var done = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Completed, done!.State);

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Collection(
            transitions,
            transition => Assert.Equal(ReasonCode.Created, transition.Reason),
            transition => Assert.Equal(ReasonCode.Claimed, transition.Reason),
            transition => Assert.Equal(ReasonCode.Completed, transition.Reason));
    }

    [Fact]
    public async Task ClaimReturnsNullWhenNoEligibleJob()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);

        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, new WorkerId("worker-x"), Lease, CorrelationId.New()),
            CancellationToken.None);

        Assert.Null(claimed);
    }
}
