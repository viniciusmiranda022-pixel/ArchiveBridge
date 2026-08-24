using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>Solicitação de freeze para um archive — NUNCA executa uma ação real (STOP-THE-LINE, req 9).</summary>
public sealed record RequestFreeze(TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, CorrelationId Correlation);

/// <summary>Resultado da solicitação: o plano vigente, seu estado e se foi CRIADO agora.</summary>
public sealed record RequestFreezeResult(FreezePlanId Plan, EvFreezeStatus Status, bool Created);

/// <summary>
/// Caso de uso de SOLICITAÇÃO de freeze (AB-4C-008 req 9; runbook §16.5 passo 31): cria (idempotente) ou
/// re-solicita (após recusa anterior) o plano de freeze do archive. NUNCA aciona nenhuma ação real no
/// Enterprise Vault — apenas registra o estado/solicitação.
/// </summary>
public sealed class RequestFreezeUseCase(IConnectorRegistry connectors, IEvFreezePlanStore freezePlans, IEvDeltaAuditTrail audit, IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IEvFreezePlanStore _freezePlans = freezePlans;
    private readonly IEvDeltaAuditTrail _audit = audit;
    private readonly IClock _clock = clock;

    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    public async Task<RequestFreezeResult> ExecuteAsync(RequestFreeze request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = await EvDeltaExecutionSupport
            .ResolveActiveConnectorAsync(_connectors, request.Scope, request.Connector, cancellationToken).ConfigureAwait(false);
        var externalArchiveId = EvDeltaExecutionSupport.SanitizeArchiveId(request.ExternalArchiveId);

        var existing = await _freezePlans.GetAsync(request.Scope, identity.Id, externalArchiveId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        if (existing is null)
        {
            var plan = EvFreezePlan.RequestFreeze(identity.Tenant, identity.Project, identity.Id, externalArchiveId);
            await _freezePlans.SaveAsync(request.Scope, plan, expectedPreviousVersion: 0, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope, new EvDeltaAuditEvent(null, null, plan.Id, EvDeltaAuditEventCode.FreezeRequested, "CREATED", request.Correlation, now),
                cancellationToken).ConfigureAwait(false);
            return new RequestFreezeResult(plan.Id, plan.Status, Created: true);
        }

        if (existing.Status == EvFreezeStatus.FreezeRejected)
        {
            var previousVersion = existing.Version;
            existing.ReRequestFreeze();
            await _freezePlans.SaveAsync(request.Scope, existing, previousVersion, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync(
                request.Scope, new EvDeltaAuditEvent(null, null, existing.Id, EvDeltaAuditEventCode.FreezeRequested, "RE_REQUESTED", request.Correlation, now),
                cancellationToken).ConfigureAwait(false);
            return new RequestFreezeResult(existing.Id, existing.Status, Created: false);
        }

        // Já solicitado/autorizado/além: idempotente, devolve o estado vigente sem transicionar (nunca regride um plano mais avançado).
        return new RequestFreezeResult(existing.Id, existing.Status, Created: false);
    }
}
