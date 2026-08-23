using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// R5-B1: o batimento (heartbeat) é PERIÓDICO real — renova o lease repetidamente DURANTE a execução
/// (não uma única vez antes dela). Estes testes provam: (a) ao menos DUAS renovações ocorrem numa mesma
/// execução mais longa que o intervalo; (b) a perda do cercamento no meio da operação cancela o token da
/// operação (nenhum efeito novo) e o desfecho é Lost; (c) ao fim da operação o laço para (sem Task
/// órfã renovando um lease de comando já encerrado); (d) uma exceção da própria operação propaga.
/// </summary>
public sealed class PlanningHeartbeatTests
{
    private static readonly LeaseCommand Lease = new(
        new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid())),
        JobId.New(),
        new WorkerId("worker-A"),
        new LeaseEpoch(1),
        CorrelationId.New());

    [Fact]
    public async Task RenewsAtLeastTwiceDuringOneExecution()
    {
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied);
        var heartbeat = new PlanningHeartbeat(leases);

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token => await Task.Delay(TimeSpan.FromMilliseconds(300), token),
            CancellationToken.None);

        Assert.False(result.Lost);
        Assert.True(result.Renewals >= 2, $"esperado ≥2 renovações na mesma execução; contou {result.Renewals}.");
        Assert.Equal(result.Renewals, leases.Calls);
    }

    [Fact]
    public async Task LostFencingMidOperationCancelsOperationAndReportsLost()
    {
        // Aplica na 1ª renovação e perde o cercamento (FencedOut) na 2ª → cancela a operação em curso.
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied, JobCommandOutcome.FencedOut);
        var heartbeat = new PlanningHeartbeat(leases);
        var effectApplied = false;

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token); // longa: será CANCELADA pela perda do cercamento
                effectApplied = true;                               // NÃO deve ocorrer
            },
            CancellationToken.None);

        Assert.True(result.Lost);
        Assert.Equal(JobCommandOutcome.FencedOut, result.LastOutcome);
        Assert.False(effectApplied); // cercamento perdido ⇒ nenhum efeito novo
    }

    [Fact]
    public async Task TransientRenewExceptionIsFailClosed()
    {
        var leases = new ThrowingLeaseManager();
        var heartbeat = new PlanningHeartbeat(leases);
        var effectApplied = false;

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                effectApplied = true;
            },
            CancellationToken.None);

        Assert.True(result.Lost); // exceção transitória no batimento ⇒ fail-closed
        Assert.False(effectApplied);
    }

    [Fact]
    public async Task LoopStopsWhenOperationEndsNoOrphanBeat()
    {
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied);
        var heartbeat = new PlanningHeartbeat(leases);

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(30),
            async token => await Task.Delay(TimeSpan.FromMilliseconds(150), token),
            CancellationToken.None);

        var afterReturn = leases.Calls;
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // se houvesse Task órfã, continuaria renovando
        Assert.Equal(afterReturn, leases.Calls);          // nenhuma renovação após o fim da operação
        Assert.False(result.Lost);
    }

    [Fact]
    public async Task OperationExceptionPropagatesAndStopsBeat()
    {
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied);
        var heartbeat = new PlanningHeartbeat(leases);

        await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(30),
            _ => throw new InvalidOperationException("erro de domínio"),
            CancellationToken.None));

        var afterThrow = leases.Calls;
        await Task.Delay(TimeSpan.FromMilliseconds(120));
        Assert.Equal(afterThrow, leases.Calls); // o laço parou junto com a operação
    }

    // ---- Renovação ADICIONAL (AB-4C-007 blocker 1 item 4: throttle lease sob o MESMO batimento) ---------

    [Fact]
    public async Task AdditionalRenewSucceedsAlongsideJobRenewalAndBothAreCalledTheSameNumberOfTimes()
    {
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied);
        var heartbeat = new PlanningHeartbeat(leases);
        var additionalCalls = 0;

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token => await Task.Delay(TimeSpan.FromMilliseconds(300), token),
            CancellationToken.None,
            onBeatAsync: _ => { Interlocked.Increment(ref additionalCalls); return Task.FromResult(true); });

        Assert.False(result.Lost);
        Assert.True(result.Renewals >= 2, $"esperado ≥2 renovações; contou {result.Renewals}.");
        Assert.Equal(result.Renewals, leases.Calls);
        Assert.Equal(result.Renewals, additionalCalls); // a renovação adicional acompanha CADA batimento do Job.
    }

    [Fact]
    public async Task AdditionalRenewFailureCancelsOperationAndReportsLostEvenWhenTheJobLeaseIsFine()
    {
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied);
        var heartbeat = new PlanningHeartbeat(leases);
        var effectApplied = false;

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token); // longa: será CANCELADA pela perda do lease adicional
                effectApplied = true;                               // NÃO deve ocorrer
            },
            CancellationToken.None,
            onBeatAsync: _ => Task.FromResult(false)); // recurso adicional (ex.: throttle) já perdido.

        Assert.True(result.Lost);
        Assert.Equal(JobCommandOutcome.Applied, result.LastOutcome); // o lease do Job em si nunca falhou.
        Assert.False(effectApplied);
    }

    [Fact]
    public async Task AdditionalRenewExceptionIsFailClosed()
    {
        var leases = new CountingLeaseManager(JobCommandOutcome.Applied);
        var heartbeat = new PlanningHeartbeat(leases);
        var effectApplied = false;

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                effectApplied = true;
            },
            CancellationToken.None,
            onBeatAsync: Task<bool> (_) => throw new InvalidOperationException("falha transitória ao renovar o recurso adicional"));

        Assert.True(result.Lost);
        Assert.False(effectApplied);
    }

    [Fact]
    public async Task AdditionalRenewIsNeverInvokedWhenTheJobLeaseItselfIsAlreadyLost()
    {
        // Perde o cercamento do Job na 1ª renovação — a adicional NUNCA deve ser tentada (o Job já não
        // está mais sob controle, então não há sentido em renovar um recurso subordinado a ele).
        var leases = new CountingLeaseManager(JobCommandOutcome.FencedOut);
        var heartbeat = new PlanningHeartbeat(leases);
        var additionalCalls = 0;

        var result = await heartbeat.RunWhileAsync(
            Lease,
            TimeSpan.FromMilliseconds(40),
            async token => await Task.Delay(TimeSpan.FromSeconds(30), token),
            CancellationToken.None,
            onBeatAsync: _ => { Interlocked.Increment(ref additionalCalls); return Task.FromResult(true); });

        Assert.True(result.Lost);
        Assert.Equal(0, additionalCalls);
    }

    private sealed class CountingLeaseManager(params JobCommandOutcome[] outcomes) : IJobLeaseManager
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var outcome = outcomes.Length == 0
                ? JobCommandOutcome.Applied
                : outcomes[Math.Min(index, outcomes.Length - 1)];
            return Task.FromResult(outcome);
        }

        public Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RecoverExpiredLeasesAsync(Workload workload, int batchSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingLeaseManager : IJobLeaseManager
    {
        public Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("falha transitória de rede/SQL no batimento");

        public Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RecoverExpiredLeasesAsync(Workload workload, int batchSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
