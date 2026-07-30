using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Tests;

public sealed class ClaimNextJobUseCaseTests
{
    private sealed class ClaimStore(ClaimedJob? result) : IJobStore
    {
        public Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(JobId.New());

        public Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobSnapshot?>(null);

        public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<JobCommandOutcome> ScheduleRetryAsync(LeaseCommand command, ErrorCode errorCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);
    }

    private static ClaimRequest Request() =>
        new(
            new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid())),
            Workload.EnterpriseVault,
            new WorkerId("w"),
            TimeSpan.FromSeconds(30),
            CorrelationId.New());

    [Fact]
    public async Task ExecuteReturnsTheClaimedJob()
    {
        var expected = new ClaimedJob(JobId.New(), new LeaseEpoch(1), DateTimeOffset.UnixEpoch, 1);
        var useCase = new ClaimNextJobUseCase(new ClaimStore(expected));

        var actual = await useCase.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ExecuteReturnsNullWhenThereIsNoWork()
    {
        var useCase = new ClaimNextJobUseCase(new ClaimStore(null));

        var actual = await useCase.ExecuteAsync(Request(), CancellationToken.None);

        Assert.Null(actual);
    }
}
