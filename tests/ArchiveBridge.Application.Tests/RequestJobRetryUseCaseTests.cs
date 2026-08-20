using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

public sealed class RequestJobRetryUseCaseTests
{
    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    [Fact]
    public async Task ExecuteDelegatesToStoreAndReturnsItsOutcome()
    {
        var store = new FakeJobStore(0) { RetryOutcome = JobRetryRequestOutcome.Applied };
        var useCase = new RequestJobRetryUseCase(store);
        var key = Guid.NewGuid();
        var request = new RequestJobRetry(Scope(), JobId.New(), key, CorrelationId.New());

        var outcome = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.Applied, outcome);
        Assert.True(store.RequestManualRetryCalled);
        Assert.Equal(key, store.LastRetryIdempotencyKey);
    }

    [Theory]
    [InlineData(JobRetryRequestOutcome.IdempotentReplay)]
    [InlineData(JobRetryRequestOutcome.NotEligible)]
    [InlineData(JobRetryRequestOutcome.NotFound)]
    [InlineData(JobRetryRequestOutcome.IdempotencyConflict)]
    public async Task ExecutePropagatesEveryStoreOutcomeVerbatim(JobRetryRequestOutcome storeOutcome)
    {
        var store = new FakeJobStore(0) { RetryOutcome = storeOutcome };
        var useCase = new RequestJobRetryUseCase(store);
        var request = new RequestJobRetry(Scope(), JobId.New(), Guid.NewGuid(), CorrelationId.New());

        var outcome = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(storeOutcome, outcome);
    }

    [Fact]
    public async Task EmptyIdempotencyKeyThrowsBeforeTouchingTheStore()
    {
        var store = new FakeJobStore(0);
        var useCase = new RequestJobRetryUseCase(store);
        var request = new RequestJobRetry(Scope(), JobId.New(), Guid.Empty, CorrelationId.New());

        await Assert.ThrowsAsync<ArgumentException>(() => useCase.ExecuteAsync(request, CancellationToken.None));

        Assert.False(store.RequestManualRetryCalled);
    }

    [Fact]
    public async Task NullRequestThrows()
    {
        var useCase = new RequestJobRetryUseCase(new FakeJobStore(0));

        await Assert.ThrowsAsync<ArgumentNullException>(() => useCase.ExecuteAsync(null!, CancellationToken.None));
    }
}
