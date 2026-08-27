using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-005 item 5 (SQL Server real) — <see cref="SqlPendingWorkRebuildQuery"/>: reconstrói trabalho
/// pendente EXCLUSIVAMENTE do estado canônico persistido (nunca de uma fila externa), emite apenas
/// comandos elegíveis, não duplica efeitos já concluídos, respeita leases/fencing/tenant/project isolation
/// e é idempotente sob reexecução concorrente (a reivindicação real permanece em
/// <see cref="IJobStore.TryClaimNextAsync"/>, já atômica).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PendingWorkRebuildIntegrationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private SqlPendingWorkRebuildQuery Rebuild() => new(fixture.Factory);

    [Fact]
    public async Task RebuildListsAPendingJobThatIsAlreadyDueAndExcludesOneScheduledInTheFuture()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var scope = SqlServerFixture.NewScope();

        var dueJobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);

        // Avança o relógio entre as criações: com a MESMA prioridade, o desempate de aging por
        // created_at_utc só é determinístico quando os instantes são estritamente distintos (mesmo padrão
        // de AntiStarvationTests) — dois Jobs com created_at_utc idêntico não têm ordem de claim garantida.
        clock.Advance(TimeSpan.FromSeconds(1));

        // Um segundo Job é criado e imediatamente reivindicado/agendado para retry no FUTURO — não deve
        // aparecer na reconstrução no instante "Start".
        var futureJobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, new WorkerId("w1"), Lease, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(dueJobId, claimed!.JobId); // aging por criação: o primeiro criado é o primeiro elegível
        await store.ScheduleRetryAsync(
            new LeaseCommand(scope, dueJobId, new WorkerId("w1"), claimed.Epoch, CorrelationId.New()), ErrorCode.TransientProvider,
            Start + TimeSpan.FromHours(2), CancellationToken.None);

        // asOf = clock.UtcNow (Start + 1s, o instante em que futureJobId foi criado) — não o "Start"
        // original, que agora antecede o próprio created_at_utc/next_attempt_at_utc de futureJobId.
        var eligible = await Rebuild().RebuildEligibleWorkAsync(scope, Workload.Pst, clock.UtcNow, CancellationToken.None);

        Assert.Single(eligible);
        Assert.Equal(futureJobId, eligible[0].Id);
        Assert.DoesNotContain(eligible, snapshot => snapshot.Id == dueJobId);
    }

    [Fact]
    public async Task AJobStuckInProcessingWithAnExpiredLeaseIsInvisibleToRebuildUntilTheReaperConvergesIt()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var leases = fixture.LeaseManager(clock, RetryPolicy.Default, Lease);
        var scope = SqlServerFixture.NewScope();

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Upload, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, new WorkerId("stuck-worker"), Lease, CorrelationId.New()), CancellationToken.None);

        clock.Advance(Lease + TimeSpan.FromSeconds(1));

        // Rebuild nunca ressuscita um lease diretamente — o Job continua Processing (não elegível) até o
        // reaper rodar; a reconstrução nunca duplica/antecipa o efeito do reaper.
        var beforeReaper = await Rebuild().RebuildEligibleWorkAsync(scope, Workload.Upload, clock.UtcNow, CancellationToken.None);
        Assert.DoesNotContain(beforeReaper, snapshot => snapshot.Id == jobId);

        var recovered = await leases.RecoverExpiredLeasesAsync(10, CancellationToken.None);
        Assert.True(recovered >= 1);

        // O reaper agenda o retry com o MESMO backoff de RetryPolicy.Default (1ª tentativa: BaseDelay) —
        // o Job só fica elegível para reconstrução quando next_attempt_at_utc chega, exatamente como o
        // primeiro teste desta classe prova para um Job agendado no futuro; a reconstrução nunca
        // antecipa o instante que o próprio reaper agendou.
        clock.Advance(RetryPolicy.Default.BaseDelay);

        var afterReaper = await Rebuild().RebuildEligibleWorkAsync(scope, Workload.Upload, clock.UtcNow, CancellationToken.None);
        Assert.Contains(afterReaper, snapshot => snapshot.Id == jobId && snapshot.State == JobState.RetryScheduled);
    }

    [Fact]
    public async Task RebuildIsScopedByTenantAndProjectAndNeverLeaksAnotherTenantsPendingWork()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var tenantAScope = SqlServerFixture.NewScope();
        var tenantBScope = SqlServerFixture.NewScope();

        var tenantAJobId = await store.CreateAsync(
            new CreateJobCommand(tenantAScope, Workload.Reconciliation, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        await store.CreateAsync(
            new CreateJobCommand(tenantBScope, Workload.Reconciliation, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);

        var eligibleForTenantA = await Rebuild().RebuildEligibleWorkAsync(tenantAScope, Workload.Reconciliation, Start, CancellationToken.None);

        Assert.Single(eligibleForTenantA);
        Assert.Equal(tenantAJobId, eligibleForTenantA[0].Id);
    }

    [Fact]
    public async Task RebuildIsAPureReadThatNeverMutatesStateAndConcurrentClaimAfterItNeverDuplicatesTheEffect()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var scope = SqlServerFixture.NewScope();
        var rebuild = Rebuild();

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);

        var firstRebuild = await rebuild.RebuildEligibleWorkAsync(scope, Workload.EnterpriseVault, Start, CancellationToken.None);
        var secondRebuild = await rebuild.RebuildEligibleWorkAsync(scope, Workload.EnterpriseVault, Start, CancellationToken.None);

        // Leitura pura: reexecutar a reconstrução não muda nada — o mesmo Job continua Pending ambas as vezes.
        Assert.Equal(firstRebuild.Select(snapshot => snapshot.Id), secondRebuild.Select(snapshot => snapshot.Id));
        Assert.All(firstRebuild, snapshot => Assert.Equal(JobState.Pending, snapshot.State));

        // Duas "reivindicações" concorrentes do MESMO trabalho listado pela reconstrução: exatamente UM
        // worker vence (a reconstrução nunca produziu o efeito por si só — o claim atômico da store
        // continua sendo a única fonte de verdade sobre quem reivindicou o quê).
        var claimTasks = new[]
        {
            store.TryClaimNextAsync(new ClaimRequest(scope, Workload.EnterpriseVault, new WorkerId("w1"), Lease, CorrelationId.New()), CancellationToken.None),
            store.TryClaimNextAsync(new ClaimRequest(scope, Workload.EnterpriseVault, new WorkerId("w2"), Lease, CorrelationId.New()), CancellationToken.None),
        };
        var claims = await Task.WhenAll(claimTasks);

        Assert.Single(claims, claim => claim is not null && claim.JobId == jobId);
        Assert.Contains(claims, claim => claim is null);
    }
}
