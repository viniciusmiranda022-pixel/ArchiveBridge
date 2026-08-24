using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>
/// Solicitação de baseline — <see cref="Scope"/> e <see cref="Connector"/> resolvidos pelo composition
/// root a partir do principal autenticado, nunca informados livremente pelo chamador como autorização.
/// </summary>
public sealed record RequestEvBaseline(TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, CorrelationId Correlation);

/// <summary>
/// Caso de uso de solicitação de BASELINE (AB-4C-008 req 1/2/5/7/8): revalida a capability EV do Passo 2,
/// seleciona a delta strategy determinística para a versão observada — fail-closed se
/// desconhecida/não elegível/ambígua, NUNCA chamando o adapter nesse caso — emite o PRIMEIRO watermark
/// via o adapter EV substituível e persiste tentativa+watermark ATOMICAMENTE, de forma idempotente por
/// (tenant/projeto/connector/archive). <c>ReceivedDate</c> nunca é usado aqui como critério: a emissão do
/// watermark é responsabilidade exclusiva do adapter da strategy selecionada.
/// </summary>
public sealed class RequestEvBaselineUseCase(
    IConnectorRegistry connectors,
    IConnectorCapabilityStore capabilities,
    IEvDeltaStrategyAdapterCatalog strategyAdapters,
    IEvDeltaRunStore runs,
    IEvDeltaAuditTrail audit,
    IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorCapabilityStore _capabilities = capabilities;
    private readonly IEvDeltaStrategyAdapterCatalog _strategyAdapters = strategyAdapters;
    private readonly IEvDeltaRunStore _runs = runs;
    private readonly IEvDeltaAuditTrail _audit = audit;
    private readonly IClock _clock = clock;

    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="Domain.EnterpriseVault.Export.EvExportCapabilityBlockedException">Capability EV não certificada/ausente.</exception>
    /// <exception cref="EvDeltaStrategyUnsupportedException">Nenhuma delta strategy elegível para a versão observada.</exception>
    public async Task<EvDeltaRunResult> ExecuteAsync(RequestEvBaseline request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var externalArchiveId = EvDeltaExecutionSupport.SanitizeArchiveId(request.ExternalArchiveId);

        var identity = await EvDeltaExecutionSupport
            .ResolveActiveConnectorAsync(_connectors, request.Scope, request.Connector, cancellationToken).ConfigureAwait(false);
        var handshake = await EvDeltaExecutionSupport
            .RequireExportCapableAsync(_capabilities, request.Scope, identity.Id, cancellationToken).ConfigureAwait(false);

        var canonicalIdentity = EvDeltaRunIdentity.Compute(
            identity.Tenant, identity.Project, identity.Id, externalArchiveId, EvDeltaPhase.Baseline, previousWatermark: null);
        var idempotencyKey = canonicalIdentity.ToIdempotencyKey();

        var latest = await _runs.GetLatestByIdempotencyKeyAsync(request.Scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (latest is not null && EvDeltaRunOutcomes.IsTerminal(latest.Outcome))
        {
            return EvDeltaExecutionSupport.ToResult(latest, replayed: true);
        }

        var now = _clock.UtcNow;
        var selection = EvDeltaStrategySelectionPolicy.Select(handshake.EvVersionDisplay, EvDeltaPhase.Baseline);
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(latest?.Run, null, null, EvDeltaAuditEventCode.StrategySelected, EvDeltaExecutionSupport.DescribeSelection(selection), request.Correlation, now),
            cancellationToken).ConfigureAwait(false);

        var adapter = selection.Outcome == EvDeltaStrategySelectionOutcome.Supported && selection.Selected is not null
            ? _strategyAdapters.Resolve(selection.Selected.StrategyId)
            : null;

        if (selection.Outcome != EvDeltaStrategySelectionOutcome.Supported || selection.Selected is null || adapter is null)
        {
            var reason = selection.Selected is not null && adapter is null
                ? $"NO_ADAPTER_REGISTERED:{selection.Selected.StrategyId.DisplayName}"
                : selection.Outcome.ToString();

            await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
                _runs, request.Scope, idempotencyKey,
                new EvDeltaAttemptCandidate(
                    latest?.Run, identity.Id, externalArchiveId, EvDeltaPhase.Baseline, Strategy: null,
                    PreviousWatermark: null, IssuedWatermark: null, EvDeltaRunOutcome.StrategyUnsupported, reason, now, now),
                watermarkToPersist: null, cancellationToken).ConfigureAwait(false);
            throw new EvDeltaStrategyUnsupportedException(reason);
        }

        var strategy = selection.Selected.StrategyId;
        var attemptId = EvDeltaAttemptId.New();

        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(latest?.Run, null, null, EvDeltaAuditEventCode.BaselineStarted, strategy.DisplayName, request.Correlation, now),
            cancellationToken).ConfigureAwait(false);

        EvWatermarkIssueResult issued;
        try
        {
            issued = await adapter.IssueBaselineWatermarkAsync(
                new EvDeltaBaselineIssueRequest(
                    request.Scope, identity.Id, externalArchiveId, handshake.EvVersionDisplay, attemptId.Value, request.Correlation),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failedAt = _clock.UtcNow;
            await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
                _runs, request.Scope, idempotencyKey,
                new EvDeltaAttemptCandidate(
                    latest?.Run, identity.Id, externalArchiveId, EvDeltaPhase.Baseline, strategy,
                    PreviousWatermark: null, IssuedWatermark: null, EvDeltaRunOutcome.Failed, EvDeltaExecutionSupport.TruncateReason(ex.Message), now, failedAt),
                watermarkToPersist: null, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope,
                new EvDeltaAuditEvent(latest?.Run, null, null, EvDeltaAuditEventCode.DeltaFailed, "BASELINE_ADAPTER_FAILURE", request.Correlation, failedAt),
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        var issuedAt = _clock.UtcNow;
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(latest?.Run, null, null, EvDeltaAuditEventCode.WatermarkIssued, strategy.DisplayName, request.Correlation, issuedAt),
            cancellationToken).ConfigureAwait(false);

        var watermark = Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, externalArchiveId, EvDeltaPhase.Baseline, strategy, attemptId.Value, issued.OpaqueToken, issuedAt);

        var completed = await EvDeltaExecutionSupport.AppendAttemptWithConvergenceAsync(
            _runs, request.Scope, idempotencyKey,
            new EvDeltaAttemptCandidate(
                latest?.Run, identity.Id, externalArchiveId, EvDeltaPhase.Baseline, strategy,
                PreviousWatermark: null, IssuedWatermark: watermark.Id, EvDeltaRunOutcome.Completed, BlockingReason: null, now, _clock.UtcNow),
            watermarkToPersist: watermark, cancellationToken).ConfigureAwait(false);

        var completedAt = _clock.UtcNow;
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(completed.Run, watermark.Id, null, EvDeltaAuditEventCode.WatermarkAccepted, strategy.DisplayName, request.Correlation, completedAt),
            cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(completed.Run, watermark.Id, null, EvDeltaAuditEventCode.BaselineCompleted, strategy.DisplayName, request.Correlation, completedAt),
            cancellationToken).ConfigureAwait(false);

        return EvDeltaExecutionSupport.ToResult(completed, replayed: false);
    }
}
