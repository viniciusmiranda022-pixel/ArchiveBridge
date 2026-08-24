using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>Tentativa de avançar para descomissionamento — SEMPRE bloqueada neste Passo (STOP-THE-LINE, req 11).</summary>
public sealed record AttemptDecommission(TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, CorrelationId Correlation);

/// <summary>
/// Caso de uso que registra explicitamente o bloqueio de descomissionamento (AB-4C-008 req 11): a ÚNICA
/// saída possível é <see cref="EvFreezeStatus.DecommissionBlocked"/> — nunca uma execução real de
/// descomissionamento/deleção. Existe para provar e auditar, de forma testável, que esta via permanece
/// fechada até sign-off/retenção/reconciliação de um Passo POSTERIOR. Idempotente: chamar novamente sobre
/// um plano já bloqueado apenas devolve o estado, sem nova transição.
/// </summary>
public sealed class AttemptDecommissionUseCase(IConnectorRegistry connectors, IEvFreezePlanStore freezePlans, IEvDeltaAuditTrail audit, IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IEvFreezePlanStore _freezePlans = freezePlans;
    private readonly IEvDeltaAuditTrail _audit = audit;
    private readonly IClock _clock = clock;

    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="EvDeltaNotFoundException">Nenhum plano de freeze para este archive.</exception>
    /// <exception cref="InvalidEvFreezeTransitionException">
    /// O plano não está em <see cref="EvFreezeStatus.RollbackRetentionRequired"/> nem já <see cref="EvFreezeStatus.DecommissionBlocked"/>.
    /// </exception>
    public async Task<EvFreezeStatus> ExecuteAsync(AttemptDecommission request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = await EvDeltaExecutionSupport
            .ResolveActiveConnectorAsync(_connectors, request.Scope, request.Connector, cancellationToken).ConfigureAwait(false);
        var externalArchiveId = EvDeltaExecutionSupport.SanitizeArchiveId(request.ExternalArchiveId);

        var plan = await _freezePlans.GetAsync(request.Scope, identity.Id, externalArchiveId, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDeltaNotFoundException("Nenhum plano de freeze para este archive.");

        if (plan.Status == EvFreezeStatus.DecommissionBlocked)
        {
            // Já bloqueado — idempotente, nenhuma nova transição/evento.
            return plan.Status;
        }

        var previousVersion = plan.Version;
        plan.BlockDecommission();
        await _freezePlans.SaveAsync(request.Scope, plan, previousVersion, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(null, null, plan.Id, EvDeltaAuditEventCode.DecommissionBlocked, null, request.Correlation, _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return plan.Status;
    }
}
