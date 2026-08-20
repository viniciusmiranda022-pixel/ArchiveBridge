using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A Passo 7 — retry manual autorizado ao nível da fila durável (<see cref="IJobStore.RequestManualRetryAsync"/>),
/// contra SQL Server real. Prova elegibilidade (somente RetryScheduled; NUNCA ressuscita Completed/Failed/Cancelled/
/// Pending/Processing), idempotência por chave, conflito determinístico (mesma chave, Job diferente), concorrência
/// (no máximo um efeito lógico) e a auditoria (job_state_transitions) emitida.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aJobRetryRequestStoreTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task RetryScheduledJobIsExpeditedToNowAndAudited()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var jobId = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(1));
        var key = Guid.NewGuid();
        var outcome = await store.RequestManualRetryAsync(scope, jobId, key, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.Applied, outcome);

        var snapshot = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.RetryScheduled, snapshot!.State); // não é uma transição de estado
        Assert.Equal(clock.UtcNow, snapshot.NextAttemptAtUtc);  // adiantado para agora

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, t => t.Reason == ReasonCode.RetryExpedited);
        var expedited = transitions.Single(t => t.Reason == ReasonCode.RetryExpedited);
        Assert.Equal(JobState.RetryScheduled, expedited.FromState);
        Assert.Equal(JobState.RetryScheduled, expedited.ToState);
    }

    [Fact]
    public async Task NeverDelaysAnAlreadyEligibleNextAttempt()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        // Agenda a retry já no PASSADO (elegível agora, aguardando o próximo poll do worker).
        var jobId = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromMinutes(-5));
        var alreadyEligibleAt = (await store.GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;

        var outcome = await store.RequestManualRetryAsync(
            scope, jobId, Guid.NewGuid(), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.Applied, outcome);
        var snapshot = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(alreadyEligibleAt, snapshot!.NextAttemptAtUtc); // nunca atrasa uma tentativa já elegível
    }

    // Cancelled não é exercitado aqui: IJobStore não expõe uma operação de cancelamento hoje (Job.Cancel é
    // domínio puro, ainda sem espelho em SQL) — a exclusão de Cancelled é coberta em JobTests (Domain.Tests),
    // que constrói o agregado diretamente em qualquer estado sem depender de infraestrutura.
    [Theory]
    [InlineData("Pending")]
    [InlineData("Processing")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public async Task NonRetryScheduledStatesAreNotEligible(string state)
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var jobId = await SeedJobInStateAsync(store, clock, scope, Enum.Parse<JobState>(state));

        var outcome = await store.RequestManualRetryAsync(
            scope, jobId, Guid.NewGuid(), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.NotEligible, outcome);
    }

    [Fact]
    public async Task NonExistentJobIsNotFound()
    {
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(new MutableClock(Start));

        var outcome = await store.RequestManualRetryAsync(
            scope, JobId.New(), Guid.NewGuid(), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task CrossProjectJobIsNotFound()
    {
        var clock = new MutableClock(Start);
        var owningScope = SqlServerFixture.NewScope();
        var otherProjectScope = new TenantScope(owningScope.Tenant, new ProjectId(Guid.NewGuid()));
        var store = fixture.Store(clock);
        var jobId = await ScheduleRetryAsync(store, clock, owningScope, TimeSpan.FromHours(1));

        var outcome = await store.RequestManualRetryAsync(
            otherProjectScope, jobId, Guid.NewGuid(), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task ReplayingTheSameKeyIsIdempotentAndAuditsOnlyOnce()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var jobId = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));
        var key = Guid.NewGuid();

        var first = await store.RequestManualRetryAsync(scope, jobId, key, CorrelationId.New(), CancellationToken.None);
        var replay = await store.RequestManualRetryAsync(scope, jobId, key, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(JobRetryRequestOutcome.Applied, first);
        Assert.Equal(JobRetryRequestOutcome.IdempotentReplay, replay);

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, t => t.Reason == ReasonCode.RetryExpedited); // nenhum efeito duplicado
    }

    // AB-7-002: prova que o ledger de idempotência é durável para CADA chave já consumida, não apenas
    // para a "mais recente" do Job. K1 aplicada, depois K2 aplicada ao MESMO job (novo efeito legítimo —
    // auto-loop RetryScheduled → RetryScheduled), e um replay TARDIO de K1 continua reconhecido como
    // idempotente — nunca reaplicado como se fosse uma chave nova.
    [Fact]
    public async Task AnEarlierKeyRemainsReplayableAfterANewerKeyIsAppliedToTheSameJob()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var jobId = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));
        var keyOne = Guid.NewGuid();
        var keyTwo = Guid.NewGuid();

        var first = await store.RequestManualRetryAsync(scope, jobId, keyOne, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(JobRetryRequestOutcome.Applied, first);

        var second = await store.RequestManualRetryAsync(scope, jobId, keyTwo, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(JobRetryRequestOutcome.Applied, second); // chave nova no MESMO job: novo efeito lógico legítimo

        var lateReplayOfKeyOne = await store.RequestManualRetryAsync(
            scope, jobId, keyOne, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(JobRetryRequestOutcome.IdempotentReplay, lateReplayOfKeyOne); // K1 nunca "expira" no ledger

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(2, transitions.Count(t => t.Reason == ReasonCode.RetryExpedited)); // exatamente K1 + K2
    }

    [Fact]
    public async Task SameKeyForADifferentJobIsADeterministicConflict()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var jobA = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));
        var jobB = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));
        var key = Guid.NewGuid();

        var first = await store.RequestManualRetryAsync(scope, jobA, key, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(JobRetryRequestOutcome.Applied, first);

        var conflict = await store.RequestManualRetryAsync(scope, jobB, key, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(JobRetryRequestOutcome.IdempotencyConflict, conflict);

        // Job B nunca foi afetado pelo conflito.
        var snapshotB = await store.GetAsync(scope, jobB, CancellationToken.None);
        Assert.NotEqual(clock.UtcNow, snapshotB!.NextAttemptAtUtc);
    }

    [Fact]
    public async Task ConcurrentRequestsWithTheSameKeyProduceAtMostOneLogicalEffect()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var jobId = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));
        var key = Guid.NewGuid();

        var results = await Task.WhenAll(
            store.RequestManualRetryAsync(scope, jobId, key, CorrelationId.New(), CancellationToken.None),
            store.RequestManualRetryAsync(scope, jobId, key, CorrelationId.New(), CancellationToken.None));

        Assert.All(results, r => Assert.True(r is JobRetryRequestOutcome.Applied or JobRetryRequestOutcome.IdempotentReplay));
        Assert.Contains(JobRetryRequestOutcome.Applied, results);

        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, t => t.Reason == ReasonCode.RetryExpedited); // um único efeito lógico

        // Mesmo resultado canônico: qualquer que tenha "vencido" a corrida, o Job reflete UM único instante
        // aplicado — não duas escritas concorrentes divergentes disputando o valor final.
        var snapshot = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(clock.UtcNow, snapshot!.NextAttemptAtUtc);
    }

    [Fact]
    public async Task EmptyIdempotencyKeyIsRejectedFailClosed()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var jobId = await ScheduleRetryAsync(store, clock, scope, TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RequestManualRetryAsync(scope, jobId, Guid.Empty, CorrelationId.New(), CancellationToken.None));
    }

    private static async Task<JobId> ScheduleRetryAsync(
        SqlJobStore store, MutableClock clock, TenantScope scope, TimeSpan nextAttemptOffset)
    {
        var worker = new WorkerId("worker-" + Guid.NewGuid().ToString("N")[..8]);
        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());
        await store.ScheduleRetryAsync(
            lease, ErrorCode.TransientProvider, clock.UtcNow + nextAttemptOffset, CancellationToken.None);
        return jobId;
    }

    private static async Task<JobId> SeedJobInStateAsync(
        SqlJobStore store, MutableClock clock, TenantScope scope, JobState targetState)
    {
        var worker = new WorkerId("worker-" + Guid.NewGuid().ToString("N")[..8]);
        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        if (targetState == JobState.Pending)
        {
            return jobId;
        }

        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());

        switch (targetState)
        {
            case JobState.Processing:
                break; // já está em Processing após o claim.
            case JobState.Completed:
                await store.CompleteAsync(lease, CancellationToken.None);
                break;
            case JobState.Failed:
                await store.FailAsync(lease, ErrorCode.PermanentProvider, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "Estado não coberto pelo seed de teste.");
        }

        return jobId;
    }
}
