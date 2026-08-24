using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Application.EnterpriseVault.Delta;

/// <summary>
/// Decisão FORMAL de um freeze solicitado — <see cref="DecidedBy"/>/<see cref="Role"/> resolvidos pelo
/// composition root a partir do operador autenticado, nunca informados livremente pelo chamador como
/// autorização. NUNCA aciona nenhuma ação real no Enterprise Vault.
/// </summary>
public sealed record DecideFreezeAuthorization(
    TenantScope Scope,
    ConnectorId Connector,
    string ExternalArchiveId,
    bool Approved,
    string DecidedBy,
    EvFreezeAuthorizationRole Role,
    string Justification,
    CorrelationId Correlation);

/// <summary>
/// Caso de uso de DECISÃO de freeze (AB-4C-008 req 10; runbook §16.5 passo 31): aprova (exigindo role
/// competente e justificativa persistidas) ou recusa formalmente um freeze já solicitado. Fail-closed:
/// role <see cref="EvFreezeAuthorizationRole.Unspecified"/> nunca autoriza; um archive fora do escopo
/// autenticado é indistinguível de inexistente (anti-IDOR, via <see cref="IConnectorRegistry.GetAsync"/>).
/// </summary>
public sealed class DecideFreezeAuthorizationUseCase(IConnectorRegistry connectors, IEvFreezePlanStore freezePlans, IEvDeltaAuditTrail audit, IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IEvFreezePlanStore _freezePlans = freezePlans;
    private readonly IEvDeltaAuditTrail _audit = audit;
    private readonly IClock _clock = clock;

    /// <exception cref="ConnectorNotFoundException">Connector inexistente/fora do escopo.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    /// <exception cref="EvDeltaNotFoundException">Nenhum plano de freeze solicitado para este archive.</exception>
    /// <exception cref="EvFreezeAuthorizationRequiredException">Aprovação sem role competente explícito.</exception>
    /// <exception cref="InvalidEvFreezeTransitionException">O plano não está em <see cref="EvFreezeStatus.FreezeRequired"/>.</exception>
    public async Task<EvFreezeStatus> ExecuteAsync(DecideFreezeAuthorization request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = await EvDeltaExecutionSupport
            .ResolveActiveConnectorAsync(_connectors, request.Scope, request.Connector, cancellationToken).ConfigureAwait(false);
        var externalArchiveId = EvDeltaExecutionSupport.SanitizeArchiveId(request.ExternalArchiveId);

        var plan = await _freezePlans.GetAsync(request.Scope, identity.Id, externalArchiveId, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDeltaNotFoundException("Nenhum plano de freeze solicitado para este archive.");

        var previousVersion = plan.Version;
        var now = _clock.UtcNow;

        if (request.Approved)
        {
            plan.AuthorizeFreeze(request.DecidedBy, request.Role, request.Justification, request.Correlation, now);
        }
        else
        {
            plan.RejectFreeze();
        }

        await _freezePlans.SaveAsync(request.Scope, plan, previousVersion, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(
            request.Scope,
            new EvDeltaAuditEvent(
                null, null, plan.Id,
                request.Approved ? EvDeltaAuditEventCode.FreezeAuthorized : EvDeltaAuditEventCode.FreezeRejected,
                request.Approved ? request.Role.ToString() : null, request.Correlation, now),
            cancellationToken).ConfigureAwait(false);

        return plan.Status;
    }
}
