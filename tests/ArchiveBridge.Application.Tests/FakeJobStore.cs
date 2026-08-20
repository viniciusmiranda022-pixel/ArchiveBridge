using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Tests;

/// <summary>Duplo de teste da porta <see cref="IJobStore"/> — prova que a Application é testável só
/// com Domain + Contracts, sem qualquer implementação de Infrastructure.</summary>
internal sealed class FakeJobStore(int attemptCount) : IJobStore
{
    public bool ScheduleRetryCalled { get; private set; }

    public bool FailCalled { get; private set; }

    public DateTimeOffset? ScheduledNextAttempt { get; private set; }

    /// <summary>Resultado devolvido por <see cref="RequestManualRetryAsync"/> — configurável pelo teste.</summary>
    public JobRetryRequestOutcome RetryOutcome { get; set; } = JobRetryRequestOutcome.Applied;

    public bool RequestManualRetryCalled { get; private set; }

    public Guid? LastRetryIdempotencyKey { get; private set; }

    public Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(JobId.New());

    public Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ClaimedJob?>(null);

    public Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken)
    {
        var snapshot = new JobSnapshot(
            jobId,
            scope.Tenant,
            scope.Project,
            Workload.Pst,
            JobPriority.Normal,
            JobState.Processing,
            new WorkerId("w"),
            new LeaseEpoch(1),
            DateTimeOffset.UnixEpoch,
            attemptCount,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return Task.FromResult<JobSnapshot?>(snapshot);
    }

    public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(JobCommandOutcome.Applied);

    public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken)
    {
        FailCalled = true;
        return Task.FromResult(JobCommandOutcome.Applied);
    }

    public Task<JobCommandOutcome> ScheduleRetryAsync(
        LeaseCommand command,
        ErrorCode errorCode,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        ScheduleRetryCalled = true;
        ScheduledNextAttempt = nextAttemptAtUtc;
        return Task.FromResult(JobCommandOutcome.Applied);
    }

    public Task<JobRetryRequestOutcome> RequestManualRetryAsync(
        TenantScope scope,
        JobId jobId,
        Guid idempotencyKey,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        RequestManualRetryCalled = true;
        LastRetryIdempotencyKey = idempotencyKey;
        return Task.FromResult(RetryOutcome);
    }
}

/// <summary>Relógio fixo para testes de use case.</summary>
internal sealed class StubClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
