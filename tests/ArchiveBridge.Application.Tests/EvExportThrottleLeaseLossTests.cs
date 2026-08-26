using ArchiveBridge.Application.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// AB-4C-007 blocker 1 item 4, testado a nível de <see cref="EvExportCommandProcessor"/> com TODOS os
/// colaboradores em memória (nenhum SQL Server real necessário — determinístico, sem depender de timing de
/// wall-clock além do intervalo curto do próprio batimento): perder a renovação do lease de THROTTLE durante
/// uma execução em curso cancela a operação (Fenced) ANTES que qualquer efeito novo seja persistido — mesmo
/// quando o lease do Job em si continua perfeitamente saudável. Prova que o novo <c>onBeatAsync</c> de
/// <see cref="ArchiveBridge.Application.Planning.PlanningHeartbeat"/> está corretamente fiado ao lease de
/// throttle (não apenas ao lease do Job, que já tinha essa proteção antes deste Passo).
/// </summary>
public sealed class EvExportThrottleLeaseLossTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMilliseconds(200); // batimento real a cada ~66ms.

    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static ConnectorIdentity ActiveConnector(TenantScope scope, DateTimeOffset now) =>
        ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('a', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);

    private static ConnectorCapabilityHandshake ExportCapableHandshake(ConnectorIdentity identity, DateTimeOffset now) =>
        ConnectorCapabilityHandshake.Rehydrate(
            CapabilityHandshakeId.New(), identity.Id, identity.Tenant, identity.Project, "15.0.0", true,
            ConnectorSupportLevel.Certified, exportCapable: true, blockingReason: null, CorrelationId.New(), now);

    [Fact]
    public async Task LosingTheThrottleLeaseDuringExecutionFencesTheOperationEvenWithAHealthyJobLease()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var identity = ActiveConnector(scope, now);
        var connectors = new FakeConnectorRegistryForExport();
        connectors.Seed(identity);
        var capabilities = new FakeConnectorCapabilityStoreForExport();
        capabilities.Seed(ExportCapableHandshake(identity, now));

        var command = new EvExportCommand(scope, identity.Id, "arch-1", 4, 18432, "operator", CorrelationId.New());
        var request = ExportRequestId.New();
        var jobId = JobId.New();
        var claimed = new ClaimedEvExportCommand(new ClaimedJob(jobId, new LeaseEpoch(1), now + LeaseDuration, 1), request, command);
        var inbox = new SingleClaimEvExportCommandInbox(claimed);

        var throttle = new ControllableThrottleLeaseStore(acquireSucceeds: true, renewSucceeds: false);
        var executor = new GateExecutor();
        var attempts = new FakeEvExportAttemptStore();
        var jobStore = new RecordingJobStoreForExport();
        var stubClock = new StubClock(now);

        var processor = new EvExportCommandProcessor(
            inbox, jobStore, new AlwaysAppliedJobLeaseManager(), connectors, capabilities, throttle, executor,
            new FakeEvExportOutputInspector([]), attempts, new FakeEvExportAuditTrail(), EvExportPolicy.Default,
            "C:\\connector-root", stubClock, RetryPolicy.Default);

        var processing = processor.ProcessNextAsync(scope, new WorkerId("w"), LeaseDuration, CorrelationId.New(), CancellationToken.None);

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // o executor FAKE começou (throttle já adquirido).
        var execution = await processing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(execution);
        Assert.Equal(EvExportCommandOutcome.Fenced, execution!.Outcome);
        Assert.True(executor.WasCanceled); // a perda do lease de throttle cancelou a execução em curso.

        var history = await attempts.ListAttemptsAsync(scope, request, CancellationToken.None);
        Assert.Empty(history); // nenhum efeito (sucesso ou qualquer outro desfecho) foi persistido — Fenced nunca escreve.
        Assert.Equal(0, jobStore.CompleteCalls); // o Job nunca foi reivindicado como concluído sob um lease de throttle perdido.
        Assert.True(throttle.ReleaseCalls >= 1); // o cleanup melhor-esforço ainda tenta liberar o lease.
    }

    // ---- Duplos de teste específicos deste cenário ---------------------------------------------------

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class SingleClaimEvExportCommandInbox(ClaimedEvExportCommand claimed) : IEvExportCommandInbox
    {
        private ClaimedEvExportCommand? _claimed = claimed;

        public Task<EvExportEnqueueResult> EnqueueIdempotentAsync(
            EvExportCommand command, Guid canonicalIdempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClaimedEvExportCommand?> TryClaimNextAsync(
            TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
        {
            var result = _claimed;
            _claimed = null; // só há UM comando disponível — a próxima chamada não encontra mais trabalho.
            return Task.FromResult(result);
        }
    }

    private sealed class AlwaysAppliedJobLeaseManager : IJobLeaseManager
    {
        public Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied); // o lease do Job em si NUNCA falha neste teste.

        public Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RecoverExpiredLeasesAsync(Workload workload, int batchSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingJobStoreForExport : IJobStore
    {
        public int CompleteCalls { get; private set; }

        public Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.FromResult(JobCommandOutcome.Applied);
        }

        public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<JobCommandOutcome> ScheduleRetryAsync(
            LeaseCommand command, ErrorCode errorCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<JobRetryRequestOutcome> RequestManualRetryAsync(
            TenantScope scope, JobId jobId, Guid idempotencyKey, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Aquisição sempre sucede (uma vez); a renovação é configurável — permite provar, de forma totalmente
    // determinística (sem depender de expiração por tempo real), que uma renovação perdida cerca a operação.
    private sealed class ControllableThrottleLeaseStore(bool acquireSucceeds, bool renewSucceeds) : IEvExportThrottleLeaseStore
    {
        public int ReleaseCalls { get; private set; }

        public Task<EvExportThrottleLease?> TryAcquireAsync(
            TenantScope scope, ConnectorId connector, string externalArchiveId, ExportAttemptId attempt,
            CorrelationId correlation, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult(acquireSucceeds ? new EvExportThrottleLease(Guid.NewGuid(), connector, externalArchiveId) : null);

        public Task<bool> TryRenewAsync(
            TenantScope scope, EvExportThrottleLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult(renewSucceeds);

        public Task ReleaseAsync(TenantScope scope, EvExportThrottleLease lease, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            return Task.CompletedTask;
        }
    }

    // Simula um executor de LONGA duração: sinaliza que começou e então bloqueia respeitando o
    // cancellationToken — nunca completa "com sucesso" dentro da janela do teste.
    private sealed class GateExecutor : IEvArchiveExportExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCanceled { get; private set; }

        public async Task<EvExportProcessResult> RunAsync(EvExportProcessRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }

            throw new InvalidOperationException("inalcançável: o delay infinito só termina por cancelamento.");
        }
    }
}
