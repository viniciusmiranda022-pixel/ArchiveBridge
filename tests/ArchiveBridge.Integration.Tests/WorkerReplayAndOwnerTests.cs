using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class WorkerReplayAndOwnerTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ADifferentWorkerWithTheSameEpochDoesNotGetIdempotentReplay()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var owner = new WorkerId("owner");
        var intruder = new WorkerId("intruder");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, owner, Lease, CorrelationId.New()),
            CancellationToken.None);

        var completed = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, owner, claimed!.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, completed);

        // Mesma época, worker DIFERENTE: não é replay do mesmo comando — deve ser barrado.
        var intruderReplay = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, intruder, claimed.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.FencedOut, intruderReplay);

        // O mesmo owner, sim, obtém replay idempotente.
        var ownerReplay = await store.CompleteAsync(
            new LeaseCommand(scope, jobId, owner, claimed.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.IdempotentReplay, ownerReplay);
    }

    [Fact]
    public async Task CompletedJobClearsOwnerButKeepsWorkerInTheAuditTrail()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var owner = new WorkerId("owner");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, owner, Lease, CorrelationId.New()),
            CancellationToken.None);
        await store.CompleteAsync(
            new LeaseCommand(scope, jobId, owner, claimed!.Epoch, CorrelationId.New()),
            CancellationToken.None);

        // Estado terminal sem owner residual nem lease.
        var job = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Completed, job!.State);
        Assert.Null(job.OwnerWorker);
        Assert.Null(job.LeaseExpiresAtUtc);

        // Mas o worker responsável permanece registrado na auditoria.
        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        var completedTransition = Assert.Single(transitions, t => t.Reason == ReasonCode.Completed);
        Assert.Equal(owner, completedTransition.Worker);
    }
}
