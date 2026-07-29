using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AntiStarvationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Aging = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task AnAgedLowPriorityJobIsClaimedAheadOfAFreshHighPriorityJob()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock, Aging);
        var worker = new WorkerId("w");

        // Job antigo de BAIXA prioridade (0) criado em T0.
        var lowPriorityOld = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Reconciliation, new JobPriority(0), CorrelationId.New()),
            CancellationToken.None);

        // Passados 11 minutos, o aging soma ~11 à prioridade efetiva do Job antigo (1/min).
        clock.Advance(TimeSpan.FromMinutes(11));

        // Job novo de ALTA prioridade (10), sem envelhecimento (efetiva 10).
        await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Reconciliation, new JobPriority(10), CorrelationId.New()),
            CancellationToken.None);

        // Prioridade efetiva: antigo 0+11=11 > novo 10 → o Job antigo NÃO passa fome.
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Reconciliation, worker, Lease, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(lowPriorityOld, claimed!.JobId);
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
}
