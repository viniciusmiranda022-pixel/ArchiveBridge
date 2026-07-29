using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Operations;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Tests;

public sealed class FailJobUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FailJobCommand Command(RetryDisposition disposition) =>
        new(
            new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid())),
            JobId.New(),
            new WorkerId("w"),
            new LeaseEpoch(1),
            ErrorCode.TransientProvider,
            disposition,
            CorrelationId.New());

    [Fact]
    public async Task TransientFailureWithAttemptsRemainingSchedulesRetry()
    {
        var store = new FakeJobStore(attemptCount: 1);
        var useCase = new FailJobUseCase(store, new StubClock(Now), RetryPolicy.Default);

        await useCase.ExecuteAsync(Command(RetryDisposition.RetryTransient), CancellationToken.None);

        Assert.True(store.ScheduleRetryCalled);
        Assert.False(store.FailCalled);
        Assert.True(store.ScheduledNextAttempt > Now);
    }

    [Fact]
    public async Task TransientFailureWithAttemptsExhaustedIsTerminal()
    {
        var store = new FakeJobStore(attemptCount: 5); // RetryPolicy.Default.MaxAttempts == 5
        var useCase = new FailJobUseCase(store, new StubClock(Now), RetryPolicy.Default);

        await useCase.ExecuteAsync(Command(RetryDisposition.RetryTransient), CancellationToken.None);

        Assert.True(store.FailCalled);
        Assert.False(store.ScheduleRetryCalled);
    }

    [Fact]
    public async Task PermanentFailureIsTerminalRegardlessOfAttempts()
    {
        var store = new FakeJobStore(attemptCount: 0);
        var useCase = new FailJobUseCase(store, new StubClock(Now), RetryPolicy.Default);

        await useCase.ExecuteAsync(Command(RetryDisposition.DoNotRetry), CancellationToken.None);

        Assert.True(store.FailCalled);
        Assert.False(store.ScheduleRetryCalled);
    }
}
