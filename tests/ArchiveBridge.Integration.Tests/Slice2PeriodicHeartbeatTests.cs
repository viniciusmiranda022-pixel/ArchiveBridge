using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// R5-B1: heartbeat PERIÓDICO real contra o SQL. Uma operação mais LONGA que o lease permanece válida
/// porque o batimento renova o lease repetidamente (ao menos DUAS renovações na mesma execução); ao fim
/// a lease continua válida (prova por uma renovação final Applied). Se o cercamento é perdido no meio
/// (lease vencido → renovação FencedOut), a operação é cancelada e nenhum efeito é aplicado.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2PeriodicHeartbeatTests(SqlServerFixture fixture)
{
    private static readonly WorkerId WorkerA = new("worker-A");

    private async Task<LeaseCommand> ClaimAsync(IClock clock, TimeSpan lease)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture, clock).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture, clock).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        var inbox = Slice2Support.Inbox(fixture, clock);
        await inbox.EnqueueAsync(PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()), CancellationToken.None);
        var claimed = await inbox.TryClaimNextAsync(scope, WorkerA, lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);
        return new LeaseCommand(scope, claimed!.Job.JobId, WorkerA, claimed.Job.Epoch, CorrelationId.New());
    }

    [Fact]
    public async Task PeriodicHeartbeatKeepsOperationLongerThanLeaseValid()
    {
        // Relógio real (wall-clock) para claim e lease manager: um lease curto (1s) e uma operação de 2.2s.
        // Sem renovação o lease expiraria; o batimento a cada 300ms (< lease) o mantém válido.
        var clock = new SystemClock();
        var leaseDuration = TimeSpan.FromSeconds(1);
        var lease = await ClaimAsync(clock, leaseDuration);
        var leaseManager = fixture.LeaseManager(clock, RetryPolicy.Default, leaseDuration);
        var heartbeat = new PlanningHeartbeat(leaseManager);

        var result = await heartbeat.RunWhileAsync(
            lease,
            TimeSpan.FromMilliseconds(300),
            async token => await Task.Delay(TimeSpan.FromMilliseconds(2200), token),
            CancellationToken.None);

        Assert.False(result.Lost);
        Assert.True(result.Renewals >= 2, $"esperado ≥2 renovações reais na mesma execução; contou {result.Renewals}.");

        // A operação durou 2.2s > lease de 1s, mas continua válida: uma renovação final é Applied.
        Assert.Equal(JobCommandOutcome.Applied, await leaseManager.RenewAsync(lease, CancellationToken.None));
    }

    [Fact]
    public async Task LostHeartbeatCancelsOperationNoEffect()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var lease = await ClaimAsync(clock, Slice2Support.Lease);
        clock.Advance(TimeSpan.FromMinutes(6)); // lease vencido: a renovação será FencedOut

        var leaseManager = fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease);
        var heartbeat = new PlanningHeartbeat(leaseManager);
        var effectApplied = false;

        var result = await heartbeat.RunWhileAsync(
            lease,
            TimeSpan.FromMilliseconds(50),
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token); // será cancelada pela perda do cercamento
                effectApplied = true;
            },
            CancellationToken.None);

        Assert.True(result.Lost);
        Assert.False(effectApplied);
    }
}
