using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-002 sobre SQL Server real: prova, contra o mecanismo de contagem de tentativas/fencing
/// realmente durável (não um duplo de teste), que <see cref="JobRetryGate"/> — o ÚNICO caminho pelo
/// qual um processador de comando agenda retry automático após falha ATIVA — converge
/// deterministicamente a um estado terminal quando o orçamento de <see cref="RetryPolicy"/> se esgota,
/// nunca reentra em RetryScheduled depois disso, nunca é acionado por um owner/época defasados, e
/// nunca é ressuscitado por uma corrida com o reaper de lease expirado.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class JobRetryGateSqlTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly RetryPolicy TightPolicy = new(MaxAttempts: 2, BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(5));
    private static readonly RetryPolicy SingleAttemptPolicy = new(MaxAttempts: 1, BaseDelay: TimeSpan.FromSeconds(1), MaxDelay: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ActiveFailureWithinBudgetSchedulesRetryAndTheJobSucceedsOnTheNextAttempt()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var worker = new WorkerId("worker-1");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var firstClaim = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var firstLease = new LeaseCommand(scope, jobId, worker, firstClaim!.Epoch, CorrelationId.New());

        var gate = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, clock, TightPolicy, firstLease, ErrorCode.TransientProvider, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(gate.RetryScheduled);
        Assert.Equal(JobCommandOutcome.Applied, gate.Outcome);
        var afterRetry = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.RetryScheduled, afterRetry!.State);
        Assert.Equal(1, afterRetry.AttemptCount);

        clock.Advance(TimeSpan.FromSeconds(2)); // passa do NextAttemptAtUtc agendado.
        var secondClaim = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(secondClaim); // elegível de novo — a nova tentativa foi de fato agendada.

        var secondLease = new LeaseCommand(scope, jobId, worker, secondClaim!.Epoch, CorrelationId.New());
        var completed = await store.CompleteAsync(secondLease, CancellationToken.None);

        Assert.Equal(JobCommandOutcome.Applied, completed);
        var final = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Completed, final!.State);
        Assert.Equal(2, final.AttemptCount);
    }

    [Fact]
    public async Task ActiveFailureThatNeverResolvesConvergesExactlyOnceToFailedWhenTheBudgetIsExhausted()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var audit = fixture.AuditReader();
        var worker = new WorkerId("worker-1");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        // Tentativa 1/2 (TightPolicy.MaxAttempts == 2): dentro do orçamento — agenda retry.
        var firstClaim = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var firstLease = new LeaseCommand(scope, jobId, worker, firstClaim!.Epoch, CorrelationId.New());
        var firstGate = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, clock, TightPolicy, firstLease, ErrorCode.TransientProvider, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(firstGate.RetryScheduled);

        // Tentativa 2/2: a MESMA causa transitória se repete — a mesma chamada que esgota o orçamento
        // converge a Failed, sem NUNCA reentrar em RetryScheduled.
        clock.Advance(TimeSpan.FromSeconds(2));
        var secondClaim = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var secondLease = new LeaseCommand(scope, jobId, worker, secondClaim!.Epoch, CorrelationId.New());
        var secondGate = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, clock, TightPolicy, secondLease, ErrorCode.TransientProvider, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(secondGate.RetryScheduled);
        Assert.Equal(JobCommandOutcome.Applied, secondGate.Outcome);

        var final = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Failed, final!.State);
        Assert.Equal(ErrorCode.ResourceExhaustion, final.LastErrorCode);
        Assert.Equal(2, final.AttemptCount);

        // Exatamente UMA transição terminal — nenhuma duplicidade de efeito/evidência de auditoria.
        var transitions = await audit.GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, transition => transition.Reason == ReasonCode.Failed);

        // Um estado terminal nunca é elegível a reivindicação — não há como o Job voltar a Processing.
        var reclaim = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        Assert.Null(reclaim);
    }

    [Fact]
    public async Task OwnerEpochStaleNeitherSchedulesRetryNorConsumesTheAttemptBudget()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var workerA = new WorkerId("worker-a");
        var workerB = new WorkerId("worker-b");
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimA = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, workerA, Lease, CorrelationId.New()),
            CancellationToken.None);
        var leaseA = new LeaseCommand(scope, jobId, workerA, claimA!.Epoch, CorrelationId.New()); // época 1 — ficará DEFASADA.

        // O worker A "cai" (nunca chega a chamar o gate): o lease expira e o reaper recupera o Job para
        // RetryScheduled SOB A MESMA ÉPOCA (o reaper nunca incrementa a época — só o claim faz isso).
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        Assert.True(await leases.RecoverExpiredLeasesAsync(64, CancellationToken.None) >= 1);

        // Passa do NextAttemptAtUtc agendado pela recuperação (RetryPolicy.Default: 30s de backoff base).
        clock.Advance(TimeSpan.FromSeconds(31));

        // Um worker DIFERENTE reivindica a nova tentativa — nova época (2), novo dono.
        var claimB = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, workerB, Lease, CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimB);
        Assert.NotEqual(claimA.Epoch, claimB!.Epoch);

        var beforeStaleCall = await store.GetAsync(scope, jobId, CancellationToken.None);

        // A chamada DEFASADA (mesma leaseA, época 1 — o worker A nunca soube que perdeu o lease) nunca
        // deveria alcançar efeito algum, mesmo que o orçamento durável (lido antes da escrita cercada, já
        // refletindo a tentativa de B) o classificasse como esgotado e tentasse convergir a Failed.
        var staleGate = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, clock, SingleAttemptPolicy, leaseA, ErrorCode.TransientProvider, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(staleGate.RetryScheduled); // orçamento (lido durável) já esgotado sob a ótica de A.
        Assert.Equal(JobCommandOutcome.FencedOut, staleGate.Outcome); // mas a ESCRITA é rejeitada por fencing.

        var afterStaleCall = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(beforeStaleCall!.State, afterStaleCall!.State);
        Assert.Equal(beforeStaleCall.AttemptCount, afterStaleCall.AttemptCount); // orçamento intocado.
        Assert.Equal(beforeStaleCall.OwnerWorker, afterStaleCall.OwnerWorker);
        Assert.Equal(beforeStaleCall.LeaseEpoch, afterStaleCall.LeaseEpoch);
        Assert.Equal(JobState.Processing, afterStaleCall.State); // ainda sob o dono/época LEGÍTIMOS (B/2).
        Assert.Equal(workerB, afterStaleCall.OwnerWorker);
    }

    [Fact]
    public async Task ConcurrentReaperRecoveryNeverResurrectsAJobThatTheGateAlreadyConvergedToFailed()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var worker = new WorkerId("worker-1");

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());

        // SingleAttemptPolicy.MaxAttempts == 1: a primeira falha ativa JÁ esgota o orçamento.
        var gate = await JobRetryGate.ScheduleRetryOrFailAsync(
            store, clock, SingleAttemptPolicy, lease, ErrorCode.TransientProvider, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.False(gate.RetryScheduled);
        var failed = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Failed, failed!.State);

        // Um heartbeat tardio sob a MESMA época/dono agora rejeitado (o Job não está mais em Processing).
        var staleHeartbeat = await leases.RenewAsync(lease, CancellationToken.None);
        Assert.Equal(JobCommandOutcome.FencedOut, staleHeartbeat);

        // O reaper GLOBAL de lease expirado (concorrente por natureza — cobre todos os workloads/projetos)
        // nunca alcança este Job: sua varredura só seleciona Jobs em Processing (state = 1); Failed é
        // terminal e permanece fora do escopo do reaper por construção da própria query.
        clock.Advance(Lease + TimeSpan.FromMinutes(1));
        _ = await leases.RecoverExpiredLeasesAsync(64, CancellationToken.None);

        var afterReaperSweep = await store.GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.Failed, afterReaperSweep!.State);
        Assert.Equal(failed.AttemptCount, afterReaperSweep.AttemptCount); // orçamento não duplicado.
        Assert.Equal(failed.LastErrorCode, afterReaperSweep.LastErrorCode);
    }
}
