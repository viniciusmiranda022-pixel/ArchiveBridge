using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>Confirmação de cutover concluído — NUNCA aciona a troca de acesso real (runbook §16.5 passo 33/34, STOP-THE-LINE).</summary>
public sealed record MarkCutoverComplete(TenantScope Scope, ConnectorId Connector, string ExternalArchiveId, CorrelationId Correlation);

/// <summary>
/// Caso de uso que registra o cutover como concluído: exige <see cref="EvFreezeStatus.FinalDeltaReady"/>
/// como precondição (fail-closed via <see cref="EvFreezeTransitions"/>) e avança o plano para
/// <see cref="EvFreezeStatus.RollbackRetentionRequired"/> — apenas ESTADO; nenhuma ação real de troca de
/// acesso ou preservação é executada por este caso de uso.
/// </summary>
public sealed class MarkCutoverCompleteUseCase(IConnectorRegistry connectors, IEvFreezePlanStore freezePlans)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IEvFreezePlanStore _freezePlans = freezePlans;

    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="EvDeltaNotFoundException">Nenhum plano de freeze para este archive.</exception>
    /// <exception cref="InvalidEvFreezeTransitionException">O plano não está em <see cref="EvFreezeStatus.FinalDeltaReady"/>.</exception>
    public async Task<EvFreezeStatus> ExecuteAsync(MarkCutoverComplete request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = await EvDeltaExecutionSupport
            .ResolveActiveConnectorAsync(_connectors, request.Scope, request.Connector, cancellationToken).ConfigureAwait(false);
        var externalArchiveId = EvDeltaExecutionSupport.SanitizeArchiveId(request.ExternalArchiveId);

        var plan = await _freezePlans.GetAsync(request.Scope, identity.Id, externalArchiveId, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDeltaNotFoundException("Nenhum plano de freeze para este archive.");

        var previousVersion = plan.Version;
        plan.MarkRollbackRetentionRequired();
        await _freezePlans.SaveAsync(request.Scope, plan, previousVersion, cancellationToken).ConfigureAwait(false);
        return plan.Status;
    }
}
