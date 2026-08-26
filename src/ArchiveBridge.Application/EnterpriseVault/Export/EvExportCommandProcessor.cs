using System.Globalization;
using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.EnterpriseVault.Export;

/// <summary>Desfecho do processamento de um comando durável de exportação.</summary>
public enum EvExportCommandOutcome
{
    /// <summary>Tentativa concluída com sucesso (efeito aplicado ou replay idempotente válido).</summary>
    Completed,

    /// <summary>
    /// Erro terminal (capability bloqueada, integridade violada, erro de domínio) OU orçamento de retry
    /// esgotado (AB-I7-002: falha transitória/throttle/concorrência que nunca se resolve converge a
    /// Failed em vez de retry indefinido): Job falhado.
    /// </summary>
    Failed,

    /// <summary>Erro transitório (falha de processo, throttle, concorrência) COM orçamento de retry disponível: nova tentativa agendada.</summary>
    Retried,

    /// <summary>Cercamento perdido: nenhum efeito persistido e a conclusão NÃO é reivindicada.</summary>
    Fenced,
}

/// <summary>Resultado do processamento de um comando de exportação reivindicado.</summary>
public sealed record EvExportCommandExecution(JobId Job, ExportRequestId Request, EvExportCommandOutcome Outcome, JobCommandOutcome JobOutcome);

/// <summary>
/// Consumidor durável dos comandos de exportação EV (AB-4C-005): reivindica o próximo Job de exportação
/// (claim EXCLUSIVO por workload <see cref="Workload.EnterpriseVault"/>), REVALIDA capability/support-matrix
/// LIVE (item 8 — nunca confia no estado do enfileiramento), adquire throttling por connector E archive
/// (item 4), invoca o adapter substituível (item 6), descobre/hasheia outputs, persiste o manifesto
/// canônico (item 12) e a história de tentativas append-only (item 11), emite custódia/auditoria (item 17),
/// e conclui/falha/retenta o Job. Heartbeat PERIÓDICO real durante toda a execução — sua perda cancela a
/// operação sem persistir efeito novo (Fenced).
/// </summary>
public sealed class EvExportCommandProcessor(
    IEvExportCommandInbox queue,
    IJobStore jobs,
    IJobLeaseManager leases,
    IConnectorRegistry connectors,
    IConnectorCapabilityStore capabilities,
    IEvExportThrottleLeaseStore throttle,
    IEvArchiveExportExecutor executor,
    IEvExportOutputInspector inspector,
    IEvExportAttemptStore attempts,
    IEvExportAuditTrail audit,
    EvExportPolicy policy,
    string connectorAuthorizedOutputRoot,
    IClock clock,
    RetryPolicy retryPolicy)
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(15);

    private readonly IEvExportCommandInbox _queue = queue;
    private readonly IJobStore _jobs = jobs;
    private readonly RetryPolicy _retryPolicy = retryPolicy;
    private readonly PlanningHeartbeat _heartbeat = new(leases);
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorCapabilityStore _capabilities = capabilities;
    private readonly IEvExportThrottleLeaseStore _throttle = throttle;
    private readonly IEvArchiveExportExecutor _executor = executor;
    private readonly IEvExportOutputInspector _inspector = inspector;
    private readonly IEvExportAttemptStore _attempts = attempts;
    private readonly IEvExportAuditTrail _audit = audit;
    private readonly EvExportPolicy _policy = policy;
    private readonly string _connectorAuthorizedOutputRoot = connectorAuthorizedOutputRoot;
    private readonly IClock _clock = clock;

    /// <summary>Reivindica e processa o próximo comando de exportação; <see langword="null"/> se não houver trabalho.</summary>
    public async Task<EvExportCommandExecution?> ProcessNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var claimed = await _queue.TryClaimNextAsync(scope, worker, leaseDuration, correlation, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return null;
        }

        var command = claimed.Command;
        var lease = new LeaseCommand(scope, claimed.Job.JobId, worker, claimed.Job.Epoch, command.Correlation);
        var fence = new JobFence(scope, claimed.Job.JobId, worker, claimed.Job.Epoch);
        var throttleHolder = new ThrottleLeaseHolder();

        EvExportRunResult? result = null;
        try
        {
            var beat = await _heartbeat.RunWhileAsync(
                lease,
                HeartbeatInterval(leaseDuration),
                async token => result = await DispatchAsync(scope, claimed, fence, leaseDuration, throttleHolder, token).ConfigureAwait(false),
                cancellationToken,
                onBeatAsync: token => RenewThrottleLeaseAsync(scope, throttleHolder, leaseDuration, token)).ConfigureAwait(false);
            if (beat.Lost)
            {
                return new EvExportCommandExecution(claimed.Job.JobId, claimed.Request, EvExportCommandOutcome.Fenced, beat.LastOutcome);
            }
        }
        catch (FencedOutException)
        {
            return new EvExportCommandExecution(claimed.Job.JobId, claimed.Request, EvExportCommandOutcome.Fenced, JobCommandOutcome.FencedOut);
        }
        catch (Exception exception) when (IsTerminal(exception))
        {
            var failed = await _jobs.FailAsync(lease, ErrorCode.Validation, cancellationToken).ConfigureAwait(false);
            return new EvExportCommandExecution(claimed.Job.JobId, claimed.Request, OutcomeFor(failed, EvExportCommandOutcome.Failed), failed);
        }
        catch (ConcurrencyException)
        {
            var gate = await JobRetryGate.ScheduleRetryOrFailAsync(
                _jobs, _clock, _retryPolicy, lease, ErrorCode.ConcurrencyLost, RetryBackoff, cancellationToken).ConfigureAwait(false);
            var gatedOutcome = gate.RetryScheduled ? EvExportCommandOutcome.Retried : EvExportCommandOutcome.Failed;
            return new EvExportCommandExecution(claimed.Job.JobId, claimed.Request, OutcomeFor(gate.Outcome, gatedOutcome), gate.Outcome);
        }

        return await FinalizeJobAsync(
            claimed.Job.JobId, claimed.Request, lease, result!, command.Correlation, cancellationToken).ConfigureAwait(false);
    }

    // Estado mutável POR CHAMADA de ProcessNextAsync (nunca compartilhado entre invocações concorrentes):
    // o laço de batimento (rodando em paralelo à operação) só pode renovar o lease de throttle DEPOIS que
    // DispatchAsync efetivamente o adquiriu — antes disso (ex.: ainda revalidando capability) não há nada a
    // renovar, e isso é um no-op válido (nunca um cercamento perdido).
    private sealed class ThrottleLeaseHolder
    {
        public EvExportThrottleLease? Lease;
    }

    // (AB-4C-007 blocker 1 item 4) Renovação do lease de throttle SOB O MESMO laço de batimento do Job:
    // perda da renovação (deadline vencido ou titular trocado) é tratada por PlanningHeartbeat exatamente
    // como perda do cercamento do Job — cancela a operação em curso ANTES que qualquer efeito novo seja
    // persistido.
    private async Task<bool> RenewThrottleLeaseAsync(
        TenantScope scope, ThrottleLeaseHolder holder, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var current = holder.Lease;
        return current is null
            || await _throttle.TryRenewAsync(scope, current, leaseDuration, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EvExportCommandExecution> FinalizeJobAsync(
        JobId jobId, ExportRequestId request, LeaseCommand lease, EvExportRunResult result, CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case EvExportAttemptOutcome.Completed:
                var completed = await _jobs.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
                return new EvExportCommandExecution(jobId, request, OutcomeFor(completed, EvExportCommandOutcome.Completed), completed);

            case EvExportAttemptOutcome.CapabilityBlocked:
            case EvExportAttemptOutcome.IntegrityFailed:
                var failed = await _jobs.FailAsync(
                    lease,
                    result.Outcome == EvExportAttemptOutcome.CapabilityBlocked ? ErrorCode.CapabilityBlocked : ErrorCode.ArtifactIntegrity,
                    cancellationToken).ConfigureAwait(false);
                return new EvExportCommandExecution(jobId, request, OutcomeFor(failed, EvExportCommandOutcome.Failed), failed);

            case EvExportAttemptOutcome.Throttled:
                var throttleGate = await JobRetryGate.ScheduleRetryOrFailAsync(
                    _jobs, _clock, _retryPolicy, lease, ErrorCode.ResourceExhaustion, ThrottleBackoff, cancellationToken).ConfigureAwait(false);
                if (throttleGate.RetryScheduled)
                {
                    await AuditRetryScheduledIfAppliedAsync(
                        lease.Scope, request, result.Attempt, correlation, throttleGate.Outcome, cancellationToken).ConfigureAwait(false);
                }

                var throttleOutcome = throttleGate.RetryScheduled ? EvExportCommandOutcome.Retried : EvExportCommandOutcome.Failed;
                return new EvExportCommandExecution(jobId, request, OutcomeFor(throttleGate.Outcome, throttleOutcome), throttleGate.Outcome);

            default: // Failed (falha transitória do processo do exporter): candidata a retry, sob orçamento.
                var retryGate = await JobRetryGate.ScheduleRetryOrFailAsync(
                    _jobs, _clock, _retryPolicy, lease, ErrorCode.TransientProvider, RetryBackoff, cancellationToken).ConfigureAwait(false);
                if (retryGate.RetryScheduled)
                {
                    await AuditRetryScheduledIfAppliedAsync(
                        lease.Scope, request, result.Attempt, correlation, retryGate.Outcome, cancellationToken).ConfigureAwait(false);
                }

                var retryOutcome = retryGate.RetryScheduled ? EvExportCommandOutcome.Retried : EvExportCommandOutcome.Failed;
                return new EvExportCommandExecution(jobId, request, OutcomeFor(retryGate.Outcome, retryOutcome), retryGate.Outcome);
        }
    }

    // (AB-4C-007 blocker 2) Audita RetryScheduled SOMENTE quando a transição do Job foi de fato aplicada (ou
    // é um replay idempotente coerente) — nunca quando o cercamento foi perdido (FencedOut/NotFound), o que
    // evitaria auditar um retry que nunca foi persistido (duplicação/evidência ambígua em replay/fenced).
    private async Task AuditRetryScheduledIfAppliedAsync(
        TenantScope scope, ExportRequestId request, ExportAttemptId attempt, CorrelationId correlation,
        JobCommandOutcome jobOutcome, CancellationToken cancellationToken)
    {
        if (jobOutcome is not (JobCommandOutcome.Applied or JobCommandOutcome.IdempotentReplay))
        {
            return;
        }

        await _audit.AppendAsync(
            scope, new EvExportAuditEvent(request, attempt, EvExportAuditEventCode.RetryScheduled, null, correlation, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan HeartbeatInterval(TimeSpan leaseDuration) =>
        leaseDuration > TimeSpan.Zero ? leaseDuration / 3.0 : TimeSpan.FromMilliseconds(1);

    private static EvExportCommandOutcome OutcomeFor(JobCommandOutcome jobOutcome, EvExportCommandOutcome appliedOutcome) =>
        jobOutcome is JobCommandOutcome.Applied or JobCommandOutcome.IdempotentReplay
            ? appliedOutcome
            : EvExportCommandOutcome.Fenced;

    // Corpo de execução SOB fencing: revalida capability, adquire throttle, invoca o executor, descobre
    // outputs, persiste attempt + auditoria. NUNCA lança para sinalizar desfecho de negócio — desfechos
    // são sempre um EvExportRunResult explícito (o Job só é transitado DEPOIS do heartbeat terminar).
    private async Task<EvExportRunResult> DispatchAsync(
        TenantScope scope, ClaimedEvExportCommand claimed, JobFence fence, TimeSpan leaseDuration,
        ThrottleLeaseHolder throttleHolder, CancellationToken cancellationToken)
    {
        var command = claimed.Command;
        var request = claimed.Request;
        var attempt = ExportAttemptId.New();
        var attemptNumber = claimed.Job.AttemptNumber;
        var startedAtUtc = _clock.UtcNow;

        // (item 8) Revalidação LIVE: o estado no momento do ENFILEIRAMENTO nunca é suficiente — o connector
        // pode ter sido revogado, ou a capability pode ter sido perdida, entre a solicitação e a execução.
        var identity = await _connectors.GetAsync(scope, command.Connector, cancellationToken).ConfigureAwait(false);
        if (identity is null || !identity.IsActive)
        {
            var blocked = EvExportEvaluator.EvaluateCapabilityBlocked(
                request, attempt, attemptNumber, "CONNECTOR_REVOKED_OR_NOT_FOUND", startedAtUtc);
            await PersistAndAuditAsync(scope, command, blocked, fence, cancellationToken).ConfigureAwait(false);
            return blocked;
        }

        var handshake = await _capabilities.GetLatestAsync(scope, command.Connector, cancellationToken).ConfigureAwait(false);
        if (handshake is null || !handshake.ExportCapable)
        {
            var blocked = EvExportEvaluator.EvaluateCapabilityBlocked(
                request, attempt, attemptNumber, handshake?.BlockingReason ?? "NO_CAPABILITY_HANDSHAKE", startedAtUtc);
            await PersistAndAuditAsync(scope, command, blocked, fence, cancellationToken).ConfigureAwait(false);
            return blocked;
        }

        // (item 4; recuperável desde AB-4C-007 blocker 1) Throttle por connector E archive — adquirido
        // ANTES de qualquer efeito externo, com deadline durável renovado pelo MESMO batimento do Job
        // (ver ThrottleLeaseHolder/RenewThrottleLeaseAsync em ProcessNextAsync).
        var acquired = await _throttle
            .TryAcquireAsync(scope, command.Connector, command.ExternalArchiveId, attempt, command.Correlation, leaseDuration, cancellationToken)
            .ConfigureAwait(false);
        if (acquired is null)
        {
            // (AB-4C-007 blocker 3) A tentativa throttled é persistida no attempt history — nunca some do
            // evidence chain — sob o MESMO fencing do restante do pipeline; um retry subsequente terá outro
            // AttemptNumber e preserva toda a lineage.
            var throttled = EvExportEvaluator.EvaluateThrottled(request, attempt, attemptNumber, byConnector: true, _clock.UtcNow);
            await PersistAndAuditAsync(scope, command, throttled, fence, cancellationToken).ConfigureAwait(false);
            return throttled;
        }

        throttleHolder.Lease = acquired;
        try
        {
            return await RunAttemptAsync(
                    scope, command, request, attempt, attemptNumber, identity.ConnectorVersion, startedAtUtc, fence, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // (AB-4C-007 blocker 1 item 5) Melhor esforço, com um token BOUNDED e INDEPENDENTE do token da
            // operação (que pode já estar cancelado — perda de cercamento, shutdown cooperativo): a
            // liberação nunca deve depender exclusivamente dele, e uma falha aqui NUNCA mascara uma exceção
            // original de RunAttemptAsync (ver ReleaseThrottleLeaseBestEffortAsync). O deadline durável do
            // lease (TryAcquireAsync/TryRenewAsync) é o fail-safe real — esta liberação é só uma otimização
            // de latência.
            throttleHolder.Lease = null;
            await ReleaseThrottleLeaseBestEffortAsync(scope, acquired).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan ThrottleReleaseTimeout = TimeSpan.FromSeconds(10);

    private async Task ReleaseThrottleLeaseBestEffortAsync(TenantScope scope, EvExportThrottleLease lease)
    {
        using var timeout = new CancellationTokenSource(ThrottleReleaseTimeout);
        try
        {
            await _throttle.ReleaseAsync(scope, lease, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Melhor esforço: liberar é uma otimização de latência, nunca uma dependência de correção — o
            // deadline durável do lease garante que ele nunca fica órfão permanentemente mesmo que esta
            // liberação falhe ou não seja tentada a tempo (crash, shutdown, timeout do cleanup). Engolida
            // deliberadamente para NUNCA mascarar a exceção original que motivou este `finally`.
        }
    }

    private async Task<EvExportRunResult> RunAttemptAsync(
        TenantScope scope, EvExportCommand command, ExportRequestId request, ExportAttemptId attempt,
        int attemptNumber, string connectorVersion, DateTimeOffset startedAtUtc, JobFence fence, CancellationToken cancellationToken)
    {
        await _audit.AppendAsync(
            scope, new EvExportAuditEvent(request, attempt, EvExportAuditEventCode.Started, null, command.Correlation, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var descriptor = new EvExportOutputDescriptor(scope, command.Connector, request, attempt);
        var location = new EvExportOutputLocation(_connectorAuthorizedOutputRoot, descriptor.RelativeToken);
        var requestedPolicy = ExportRequestPolicy.Create(command.MaxPstSizeMb, command.MaxThreads);
        var arguments = EvExportCommandArguments.Create(command.ExternalArchiveId, descriptor.RelativeToken, requestedPolicy);

        EvExportRunResult result;
        try
        {
            var process = await _executor
                .RunAsync(
                    new EvExportProcessRequest(arguments, location, connectorVersion, _policy.ProcessTimeout, _policy.MaxOutputBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            result = process.ExitCode != 0 || process.TimedOut || process.OutputLimitExceeded
                ? EvExportEvaluator.EvaluateProcessFailure(request, attempt, attemptNumber, process, startedAtUtc, _clock.UtcNow)
                : await BuildSuccessResultAsync(
                    request, attempt, attemptNumber, command.ExternalArchiveId, process, location, startedAtUtc, scope, command,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (EvExportOutputContainmentException containment)
        {
            result = EvExportEvaluator.EvaluateIntegrityFailed(
                request, attempt, attemptNumber, EvExportResultCodes.OutputPathEscape, containment.Message, startedAtUtc, _clock.UtcNow);
        }

        await PersistAndAuditAsync(scope, command, result, fence, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<EvExportRunResult> BuildSuccessResultAsync(
        ExportRequestId request, ExportAttemptId attempt, int attemptNumber, string externalArchiveId,
        EvExportProcessResult process, EvExportOutputLocation location, DateTimeOffset startedAtUtc,
        TenantScope scope, EvExportCommand command, CancellationToken cancellationToken)
    {
        var candidates = await _inspector.ScanPstOutputsAsync(location, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            scope,
            new EvExportAuditEvent(
                request, attempt, EvExportAuditEventCode.OutputDiscovered, candidates.Count.ToString(CultureInfo.InvariantCulture),
                command.Correlation, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var completedAtUtc = _clock.UtcNow;
        var result = EvExportEvaluator.EvaluateSuccess(
            request, attempt, attemptNumber, externalArchiveId, process, candidates, startedAtUtc, completedAtUtc);

        if (result.OversizedItems.Count > 0)
        {
            await _audit.AppendAsync(
                scope,
                new EvExportAuditEvent(
                    request, attempt, EvExportAuditEventCode.OversizedDetected,
                    result.OversizedItems.Count.ToString(CultureInfo.InvariantCulture), command.Correlation, _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task PersistAndAuditAsync(
        TenantScope scope, EvExportCommand command, EvExportRunResult result, JobFence fence,
        CancellationToken cancellationToken)
    {
        var record = new EvExportAttemptRecord(
            result.Request, result.Attempt, result.AttemptNumber, command.Connector, command.ExternalArchiveId,
            result.Outcome, result.ResultCode, result.BlockingReason, result.Manifest, result.OversizedItems,
            result.ProcessExitCode, result.StartedAtUtc, result.CompletedAtUtc);
        await _attempts.AppendAsync(scope, record, fence, cancellationToken).ConfigureAwait(false);

        var eventCode = result.Outcome switch
        {
            EvExportAttemptOutcome.Completed => EvExportAuditEventCode.Completed,
            EvExportAttemptOutcome.IntegrityFailed => EvExportAuditEventCode.IntegrityFailed,
            EvExportAttemptOutcome.Throttled => EvExportAuditEventCode.Throttled,
            _ => EvExportAuditEventCode.Failed,
        };
        await _audit.AppendAsync(
            scope,
            new EvExportAuditEvent(result.Request, result.Attempt, eventCode, result.ResultCode.Value, command.Correlation, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(Exception exception) =>
        exception is EvExportValidationException
            or EvExportNotFoundException
            or EvExportManifestValidationException
            or EvExportIdempotencyConflictException
            or ArgumentException;
}
