using System.Globalization;
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

    /// <summary>Erro terminal (capability bloqueada, integridade violada, erro de domínio): Job falhado.</summary>
    Failed,

    /// <summary>Erro transitório (falha de processo, throttle, concorrência): nova tentativa agendada.</summary>
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
    IClock clock)
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(15);

    private readonly IEvExportCommandInbox _queue = queue;
    private readonly IJobStore _jobs = jobs;
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

        EvExportRunResult? result = null;
        try
        {
            var beat = await _heartbeat.RunWhileAsync(
                lease,
                HeartbeatInterval(leaseDuration),
                async token => result = await DispatchAsync(scope, claimed, fence, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
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
            var retried = await _jobs.ScheduleRetryAsync(
                lease, ErrorCode.ConcurrencyLost, _clock.UtcNow + RetryBackoff, cancellationToken).ConfigureAwait(false);
            return new EvExportCommandExecution(claimed.Job.JobId, claimed.Request, OutcomeFor(retried, EvExportCommandOutcome.Retried), retried);
        }

        return await FinalizeJobAsync(claimed.Job.JobId, claimed.Request, lease, result!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EvExportCommandExecution> FinalizeJobAsync(
        JobId jobId, ExportRequestId request, LeaseCommand lease, EvExportRunResult result, CancellationToken cancellationToken)
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
                var throttled = await _jobs.ScheduleRetryAsync(
                    lease, ErrorCode.ResourceExhaustion, _clock.UtcNow + ThrottleBackoff, cancellationToken).ConfigureAwait(false);
                return new EvExportCommandExecution(jobId, request, OutcomeFor(throttled, EvExportCommandOutcome.Retried), throttled);

            default: // Failed (falha transitória do processo do exporter): candidata a retry.
                var retried = await _jobs.ScheduleRetryAsync(
                    lease, ErrorCode.TransientProvider, _clock.UtcNow + RetryBackoff, cancellationToken).ConfigureAwait(false);
                return new EvExportCommandExecution(jobId, request, OutcomeFor(retried, EvExportCommandOutcome.Retried), retried);
        }
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
        TenantScope scope, ClaimedEvExportCommand claimed, JobFence fence, CancellationToken cancellationToken)
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

        // (item 4) Throttle por connector E archive — adquirido ANTES de qualquer efeito externo.
        var lease = await _throttle
            .TryAcquireAsync(scope, command.Connector, command.ExternalArchiveId, attempt, command.Correlation, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            await _audit.AppendAsync(
                scope,
                new EvExportAuditEvent(request, attempt, EvExportAuditEventCode.Throttled, null, command.Correlation, _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
            return EvExportEvaluator.EvaluateThrottled(request, attempt, attemptNumber, byConnector: true, _clock.UtcNow);
        }

        try
        {
            return await RunAttemptAsync(
                    scope, command, request, attempt, attemptNumber, identity.ConnectorVersion, startedAtUtc, fence, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await _throttle.ReleaseAsync(scope, lease, cancellationToken).ConfigureAwait(false);
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
