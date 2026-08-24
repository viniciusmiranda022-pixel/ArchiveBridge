using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>
/// Solicitação de delta incremental ou delta final — <see cref="Phase"/> deve ser
/// <see cref="EvDeltaPhase.Delta"/> ou <see cref="EvDeltaPhase.FinalDelta"/> (nunca Baseline, que tem seu
/// próprio caso de uso). <see cref="Scope"/>/<see cref="Connector"/> resolvidos pelo composition root.
/// </summary>
public sealed record RequestEvDelta(TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, EvDeltaPhase Phase, CorrelationId Correlation);

/// <summary>
/// Caso de uso de solicitação de DELTA/FINAL DELTA (AB-4C-008 req 1/2/5/7/8/9/10/12/13/14): exige um
/// watermark canônico anterior (baseline já executado), revalida capability EV, seleciona a delta
/// strategy determinística, garante que o watermark anterior pode preceder a nova strategy/versão
/// (fail-closed em stale/cross-scope/downgrade), emite o próximo watermark e persiste
/// tentativa+watermark atomicamente. <see cref="EvDeltaPhase.FinalDelta"/> exige adicionalmente um
/// plano de freeze já <see cref="EvFreezeStatus.FreezeAuthorized"/> (STOP-THE-LINE: nenhum delta final
/// fora da janela de freeze autorizada) e, ao concluir, marca o plano
/// <see cref="EvFreezeStatus.FinalDeltaReady"/>.
/// </summary>
public sealed class RequestEvDeltaUseCase(
    IConnectorRegistry connectors,
    IConnectorCapabilityStore capabilities,
    IEvWatermarkStore watermarks,
    IEvFreezePlanStore freezePlans,
    IEvDeltaStrategyAdapterCatalog strategyAdapters,
    IEvDeltaRunStore runs,
    IEvDeltaAuditTrail audit,
    IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorCapabilityStore _capabilities = capabilities;
    private readonly IEvWatermarkStore _watermarks = watermarks;
    private readonly IEvFreezePlanStore _freezePlans = freezePlans;
    private readonly IEvDeltaStrategyAdapterCatalog _strategyAdapters = strategyAdapters;
    private readonly IEvDeltaRunStore _runs = runs;
    private readonly IEvDeltaAuditTrail _audit = audit;
    private readonly IClock _clock = clock;

    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="Domain.EnterpriseVault.Export.EvExportCapabilityBlockedException">Capability EV não certificada/ausente.</exception>
    /// <exception cref="EvDeltaValidationException"><see cref="RequestEvDelta.Phase"/> inválida ou nenhum baseline anterior.</exception>
    /// <exception cref="EvFreezeNotAuthorizedException">FinalDelta solicitado sem freeze autorizado.</exception>
    /// <exception cref="EvDeltaStrategyUnsupportedException">Nenhuma delta strategy elegível para a versão observada.</exception>
    /// <exception cref="EvWatermarkRejectedException">Watermark anterior stale/cross-scope/downgrade ou watermark emitido não é mais recente.</exception>
    public async Task<EvDeltaRunResult> ExecuteAsync(RequestEvDelta request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Phase != EvDeltaPhase.Delta && request.Phase != EvDeltaPhase.FinalDelta)
        {
            throw new EvDeltaValidationException("RequestEvDelta exige Phase Delta ou FinalDelta (Baseline tem caso de uso próprio).");
        }

        var externalArchiveId = EvDeltaExecutionSupport.SanitizeArchiveId(request.ExternalArchiveId);
        var identity = await EvDeltaExecutionSupport
            .ResolveActiveConnectorAsync(_connectors, request.Scope, request.Connector, cancellationToken).ConfigureAwait(false);
        var handshake = await EvDeltaExecutionSupport
            .RequireExportCapableAsync(_capabilities, request.Scope, identity.Id, cancellationToken).ConfigureAwait(false);

        var previous = await _watermarks.GetLatestCanonicalAsync(request.Scope, identity.Id, externalArchiveId, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDeltaValidationException("Nenhum watermark canônico anterior — execute o Baseline antes de solicitar Delta.");

        EvFreezePlan? freezePlan = null;
        if (request.Phase == EvDeltaPhase.FinalDelta)
        {
            freezePlan = await _freezePlans.GetAsync(request.Scope, identity.Id, externalArchiveId, cancellationToken).ConfigureAwait(false);
            if (freezePlan is null || freezePlan.Status != EvFreezeStatus.FreezeAuthorized)
            {
                throw new EvFreezeNotAuthorizedException(
                    "FinalDelta recusado: nenhum freeze formalmente autorizado para este archive (fail-closed).");
            }
        }

        var canonicalIdentity = EvDeltaRunIdentity.Compute(
            identity.Tenant, identity.Project, identity.Id, externalArchiveId, request.Phase, previous.Id);
        var idempotencyKey = canonicalIdentity.ToIdempotencyKey();

        var latest = await _runs.GetLatestByIdempotencyKeyAsync(request.Scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (latest is not null && EvDeltaRunOutcomes.IsTerminal(latest.Outcome))
        {
            return EvDeltaExecutionSupport.ToResult(latest, replayed: true);
        }

        var now = _clock.UtcNow;
        var selection = EvDeltaStrategySelectionPolicy.Select(handshake.EvVersionDisplay, request.Phase);
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(latest?.Run, previous.Id, freezePlan?.Id, EvDeltaAuditEventCode.StrategySelected, EvDeltaExecutionSupport.DescribeSelection(selection), request.Correlation, now),
            cancellationToken).ConfigureAwait(false);

        var adapter = selection.Outcome == EvDeltaStrategySelectionOutcome.Supported && selection.Selected is not null
            ? _strategyAdapters.Resolve(selection.Selected.StrategyId)
            : null;

        if (selection.Outcome != EvDeltaStrategySelectionOutcome.Supported || selection.Selected is null || adapter is null)
        {
            var unsupportedReason = selection.Selected is not null && adapter is null
                ? $"NO_ADAPTER_REGISTERED:{selection.Selected.StrategyId.DisplayName}"
                : selection.Outcome.ToString();
            await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
                _runs, request.Scope, idempotencyKey,
                new EvDeltaAttemptCandidate(
                    latest?.Run, identity.Id, externalArchiveId, request.Phase, Strategy: null,
                    PreviousWatermark: previous.Id, IssuedWatermark: null, EvDeltaRunOutcome.StrategyUnsupported, unsupportedReason, now, now),
                watermarkToPersist: null, cancellationToken).ConfigureAwait(false);
            throw new EvDeltaStrategyUnsupportedException(unsupportedReason);
        }

        var strategy = selection.Selected.StrategyId;

        try
        {
            previous.EnsureCanPrecede(identity.Tenant, identity.Project, identity.Id, externalArchiveId, strategy);
        }
        catch (EvWatermarkRejectedException ex)
        {
            await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
                _runs, request.Scope, idempotencyKey,
                new EvDeltaAttemptCandidate(
                    latest?.Run, identity.Id, externalArchiveId, request.Phase, strategy,
                    PreviousWatermark: previous.Id, IssuedWatermark: null, EvDeltaRunOutcome.WatermarkRejected, ex.Reason.ToString(), now, now),
                watermarkToPersist: null, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope,
                new EvDeltaAuditEvent(latest?.Run, previous.Id, freezePlan?.Id, EvDeltaAuditEventCode.WatermarkRejected, ex.Reason.ToString(), request.Correlation, now),
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        var attemptId = EvDeltaAttemptId.New();
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(latest?.Run, previous.Id, freezePlan?.Id, EvDeltaAuditEventCode.DeltaRequested, strategy.DisplayName, request.Correlation, now),
            cancellationToken).ConfigureAwait(false);

        EvWatermarkIssueResult issued;
        try
        {
            issued = await adapter.IssueIncrementalWatermarkAsync(
                new EvDeltaIncrementIssueRequest(
                    request.Scope, identity.Id, externalArchiveId, handshake.EvVersionDisplay, previous, attemptId.Value, request.Correlation),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failedAt = _clock.UtcNow;
            await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
                _runs, request.Scope, idempotencyKey,
                new EvDeltaAttemptCandidate(
                    latest?.Run, identity.Id, externalArchiveId, request.Phase, strategy,
                    PreviousWatermark: previous.Id, IssuedWatermark: null, EvDeltaRunOutcome.Failed, EvDeltaExecutionSupport.TruncateReason(ex.Message), now, failedAt),
                watermarkToPersist: null, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope,
                new EvDeltaAuditEvent(latest?.Run, previous.Id, freezePlan?.Id, EvDeltaAuditEventCode.DeltaFailed, "DELTA_ADAPTER_FAILURE", request.Correlation, failedAt),
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        var issuedAt = _clock.UtcNow;
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(latest?.Run, previous.Id, freezePlan?.Id, EvDeltaAuditEventCode.WatermarkIssued, strategy.DisplayName, request.Correlation, issuedAt),
            cancellationToken).ConfigureAwait(false);

        var candidateWatermark = Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, externalArchiveId, request.Phase, strategy, attemptId.Value, issued.OpaqueToken, issuedAt);

        try
        {
            previous.EnsureSucceededBy(candidateWatermark);
        }
        catch (EvWatermarkRejectedException ex)
        {
            var rejectedAt = _clock.UtcNow;
            await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
                _runs, request.Scope, idempotencyKey,
                new EvDeltaAttemptCandidate(
                    latest?.Run, identity.Id, externalArchiveId, request.Phase, strategy,
                    PreviousWatermark: previous.Id, IssuedWatermark: null, EvDeltaRunOutcome.WatermarkRejected, ex.Reason.ToString(), now, rejectedAt),
                watermarkToPersist: null, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope,
                new EvDeltaAuditEvent(latest?.Run, previous.Id, freezePlan?.Id, EvDeltaAuditEventCode.WatermarkRejected, ex.Reason.ToString(), request.Correlation, rejectedAt),
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        var completed = await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
            _runs, request.Scope, idempotencyKey,
            new EvDeltaAttemptCandidate(
                latest?.Run, identity.Id, externalArchiveId, request.Phase, strategy,
                PreviousWatermark: previous.Id, IssuedWatermark: candidateWatermark.Id, EvDeltaRunOutcome.Completed, BlockingReason: null, now, _clock.UtcNow),
            watermarkToPersist: candidateWatermark, cancellationToken).ConfigureAwait(false);

        var completedAt = _clock.UtcNow;
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(completed.Run, candidateWatermark.Id, freezePlan?.Id, EvDeltaAuditEventCode.WatermarkAccepted, strategy.DisplayName, request.Correlation, completedAt),
            cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(completed.Run, candidateWatermark.Id, freezePlan?.Id, EvDeltaAuditEventCode.DeltaCompleted, strategy.DisplayName, request.Correlation, completedAt),
            cancellationToken).ConfigureAwait(false);

        if (request.Phase == EvDeltaPhase.FinalDelta && completed.Outcome == EvDeltaRunOutcome.Completed && freezePlan is not null)
        {
            var previousVersion = freezePlan.Version;
            freezePlan.MarkFinalDeltaReady();
            await _freezePlans.SaveAsync(request.Scope, freezePlan, previousVersion, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope,
                new EvDeltaAuditEvent(completed.Run, candidateWatermark.Id, freezePlan.Id, EvDeltaAuditEventCode.FinalDeltaReady, null, request.Correlation, _clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        return EvDeltaExecutionSupport.ToResult(completed, replayed: false);
    }
}
