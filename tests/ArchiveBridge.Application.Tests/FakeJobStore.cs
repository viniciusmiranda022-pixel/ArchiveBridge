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

    public ErrorCode? LastFailErrorCode { get; private set; }

    public ErrorCode? LastScheduleRetryErrorCode { get; private set; }

    /// <summary>Quantas vezes GetAsync devolveu um snapshot (nulo se nunca chamado) — prova que o
    /// orçamento é consultado NO MÁXIMO uma vez por decisão (AB-I7-002).</summary>
    public int GetAsyncCallCount { get; private set; }

    /// <summary>Quando não-nulo, GetAsync devolve <see langword="null"/> (job inexistente/fora de escopo)
    /// em vez do snapshot sintético — usado para provar o comportamento fail-closed de JobRetryGate
    /// quando o orçamento não pode ser verificado.</summary>
    public bool ReturnNullSnapshot { get; set; }

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
        GetAsyncCallCount++;
        if (ReturnNullSnapshot)
        {
            return Task.FromResult<JobSnapshot?>(null);
        }

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
        LastFailErrorCode = errorCode;
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
        LastScheduleRetryErrorCode = errorCode;
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
