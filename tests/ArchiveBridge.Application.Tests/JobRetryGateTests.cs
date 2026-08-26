using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// AB-I7-002: <see cref="JobRetryGate"/> é o único ponto de decisão de orçamento de retry para falhas
/// ATIVAS reportadas por um processador de comando. Antes deste gate, os processadores chamavam
/// <see cref="IJobStore.ScheduleRetryAsync"/> diretamente para toda falha ativa, sem nunca consultar
/// <see cref="RetryPolicy"/> — um Job cuja causa de falha nunca se resolvesse podia oscilar
/// indefinidamente entre Processing/RetryScheduled sem jamais convergir a um estado terminal. Estes
/// testes provam que o gate fecha essa lacuna.
/// </summary>
public sealed class JobRetryGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static LeaseCommand Lease() =>
        new(Scope, JobId.New(), new WorkerId("w"), new LeaseEpoch(1), CorrelationId.New());

    [Fact]
    public async Task TransientFailureWithAttemptsRemainingSchedulesRetryUsingTheProvidedBackoff()
    {
        var store = new FakeJobStore(attemptCount: 1); // RetryPolicy.Default.MaxAttempts == 5.

        var result = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, new StubClock(Now), RetryPolicy.Default, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.RetryScheduled);
        Assert.True(store.ScheduleRetryCalled);
        Assert.False(store.FailCalled);
        Assert.Equal(ErrorCode.TransientProvider, store.LastScheduleRetryErrorCode);
        Assert.Equal(Now + TimeSpan.FromSeconds(30), store.ScheduledNextAttempt);
    }

    [Fact]
    public async Task ActiveFailureThatNeverResolvesConvergesExactlyOnceToFailedWhenTheBudgetIsExhausted()
    {
        // AB-I7-002: a causa de falha nunca se resolve (ex.: SAS permanentemente consumido) — sem o gate,
        // isso oscilaria Processing/RetryScheduled para sempre. Com o gate, ao esgotar o orçamento a
        // MESMA chamada converge atomicamente a Failed — nunca reentra em RetryScheduled.
        var store = new FakeJobStore(attemptCount: 5); // RetryPolicy.Default.MaxAttempts == 5 ⇒ esgotado.

        var result = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, new StubClock(Now), RetryPolicy.Default, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.RetryScheduled);
        Assert.True(store.FailCalled);
        Assert.False(store.ScheduleRetryCalled);
        // Código estável de esgotamento de orçamento — o MESMO já usado pelo reaper de lease expirado
        // (SqlJobLeaseManager) — nunca o último ErrorCode transitório observado, que variaria a cada causa.
        Assert.Equal(ErrorCode.ResourceExhaustion, store.LastFailErrorCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RemainsRetryScheduledForEveryAttemptStrictlyBelowMaxAttempts(int attemptCount)
    {
        var store = new FakeJobStore(attemptCount);

        var result = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, new StubClock(Now), RetryPolicy.Default, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.RetryScheduled);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(100)]
    public async Task ConvergesToFailedForEveryAttemptAtOrAboveMaxAttempts(int attemptCount)
    {
        var store = new FakeJobStore(attemptCount);

        var result = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, new StubClock(Now), RetryPolicy.Default, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.RetryScheduled);
    }

    [Fact]
    public async Task AJobThatCannotBeReadFailsClosedRatherThanRetryingBlindly()
    {
        // Sem o snapshot durável, o orçamento não pode ser verificado — fail-closed (nunca assume que há
        // orçamento disponível). A própria escrita de FailAsync ainda está sob fencing normalmente.
        var store = new FakeJobStore(attemptCount: 0) { ReturnNullSnapshot = true };

        var result = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, new StubClock(Now), RetryPolicy.Default, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.RetryScheduled);
        Assert.True(store.FailCalled);
        Assert.False(store.ScheduleRetryCalled);
    }

    [Fact]
    public async Task ConsultsTheDurableAttemptCountAtMostOncePerDecision()
    {
        var store = new FakeJobStore(attemptCount: 1);

        await JobRetryGate.ScheduleRetryOrFailAsync(
            store, new StubClock(Now), RetryPolicy.Default, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(1, store.GetAsyncCallCount);
    }

    [Fact]
    public async Task ADifferentRetryPolicyMaxAttemptsIsHonoredRatherThanAHardcodedBudget()
    {
        // Prova que o orçamento vem de RetryPolicy (parametrizado pelo chamador), não de uma constante
        // fixa embutida no gate.
        var tightPolicy = new RetryPolicy(MaxAttempts: 2, BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(5));
        var withinBudget = new FakeJobStore(attemptCount: 1);
        var exhausted = new FakeJobStore(attemptCount: 2);

        var withinResult = await JobRetryGate.ScheduleRetryOrFailAsync(
            withinBudget, new StubClock(Now), tightPolicy, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);
        var exhaustedResult = await JobRetryGate.ScheduleRetryOrFailAsync(
            exhausted, new StubClock(Now), tightPolicy, Lease(), ErrorCode.TransientProvider,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(withinResult.RetryScheduled);
        Assert.False(exhaustedResult.RetryScheduled);
    }
}
