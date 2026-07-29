using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AntiStarvationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OneSecondAging = TimeSpan.FromSeconds(1);

    // Limite máximo de espera documentado = (Max - Min) * agingInterval.
    private static TimeSpan MaxWait(TimeSpan agingInterval) =>
        TimeSpan.FromSeconds((JobPriority.MaxValue - JobPriority.MinValue) * agingInterval.TotalSeconds);

    [Fact]
    public async Task AnAgedLowPriorityJobIsClaimedWithinTheDocumentedLimitDespiteFreshHighPriorityArrivals()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock, OneSecondAging);
        var worker = new WorkerId("w");

        var lowOld = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Reconciliation, new JobPriority(JobPriority.MinValue), CorrelationId.New()),
            CancellationToken.None);

        // Espera até o limite documentado; nesse instante chegam vários Jobs NOVOS de alta prioridade.
        clock.Advance(MaxWait(OneSecondAging));
        for (var i = 0; i < 5; i++)
        {
            await store.CreateAsync(
                new CreateJobCommand(scope, Workload.Reconciliation, JobPriority.Highest, CorrelationId.New()),
                CancellationToken.None);
        }

        // Prioridade efetiva: antigo 0 + 100 = 100 (empata com os novos 100) e vence pela idade.
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Reconciliation, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(lowOld, claimed!.JobId);
    }

    [Fact]
    public async Task AgingExpressionDoesNotOverflowOverVeryLongWaits()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock, OneSecondAging);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, new JobPriority(0), CorrelationId.New()),
            CancellationToken.None);

        // ~100 anos de espera estouraria DATEDIFF(SECOND, INT); DATEDIFF_BIG (BIGINT) não estoura.
        clock.Advance(TimeSpan.FromDays(365 * 100));
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Control, new WorkerId("w"), Lease, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(jobId, claimed!.JobId);
    }

    [Fact]
    public async Task WithoutAgingHighPriorityWinsOverAFreshLowPriorityJob()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock, TimeSpan.FromDays(3650)); // aging desprezível
        var worker = new WorkerId("w");

        await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, new JobPriority(0), CorrelationId.New()),
            CancellationToken.None);
        var high = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, new JobPriority(10), CorrelationId.New()),
            CancellationToken.None);

        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Control, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(high, claimed!.JobId);
    }

    [Fact]
    public void AgingIntervalMustBeStrictlyPositive()
    {
        var clock = new MutableClock(Start);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlJobStore(fixture.Factory, clock, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlJobStore(fixture.Factory, clock, TimeSpan.FromSeconds(-5)));
    }
}
