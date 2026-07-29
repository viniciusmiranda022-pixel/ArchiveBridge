using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Operations;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class IdempotencyAndRetryTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ReplayingCompleteIsIdempotentAndAuditsOnce()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var worker = new WorkerId("worker-1");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());

        var first = await store.CompleteAsync(lease, CancellationToken.None);
        var replay = await store.CompleteAsync(lease, CancellationToken.None);

        Assert.Equal(JobCommandOutcome.Applied, first);
        Assert.Equal(JobCommandOutcome.IdempotentReplay, replay);

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, transition => transition.Reason == ReasonCode.Completed);
    }

    [Fact]
    public async Task ReplayingScheduleRetryIsIdempotent()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var worker = new WorkerId("worker-1");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());
        var next = clock.UtcNow + TimeSpan.FromMinutes(1);

        var first = await store.ScheduleRetryAsync(lease, ErrorCode.TransientProvider, next, CancellationToken.None);
        var replay = await store.ScheduleRetryAsync(lease, ErrorCode.TransientProvider, next, CancellationToken.None);

        Assert.Equal(JobCommandOutcome.Applied, first);
        Assert.Equal(JobCommandOutcome.IdempotentReplay, replay);
    }

    [Fact]
    public async Task TransientFailureSchedulesRetryWithBackoff()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var worker = new WorkerId("worker-1");
        var failUseCase = new FailJobUseCase(store, clock, RetryPolicy.Default);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        var outcome = await failUseCase.ExecuteAsync(
            new FailJobCommand(scope, jobId, worker, claimed!.Epoch, ErrorCode.TransientProvider,
                RetryDisposition.RetryTransient, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(JobCommandOutcome.Applied, outcome);
        var job = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.RetryScheduled, job!.State);
        Assert.NotNull(job.NextAttemptAtUtc);
        Assert.True(job.NextAttemptAtUtc > clock.UtcNow);
    }

    [Fact]
    public async Task PermanentFailureIsTerminal()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var worker = new WorkerId("worker-1");
        var failUseCase = new FailJobUseCase(store, clock, RetryPolicy.Default);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        var outcome = await failUseCase.ExecuteAsync(
            new FailJobCommand(scope, jobId, worker, claimed!.Epoch, ErrorCode.PermanentProvider,
                RetryDisposition.DoNotRetry, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(JobCommandOutcome.Applied, outcome);
        var job = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Failed, job!.State);
    }
}
