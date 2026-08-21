using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Application.EnterpriseVault.Connector;

/// <summary>
/// Solicitação de submissão — <see cref="Scope"/> e <see cref="AuthenticatedConnector"/> são resolvidos
/// juntos pelo composition root a partir do transporte autenticado do connector (mesmo padrão de
/// <c>IPortalScopeAccessor</c> para principals do Portal); nunca informados livremente pelo chamador.
/// </summary>
public sealed record SubmitInventorySnapshotRequest(TenantScope Scope, ConnectorId AuthenticatedConnector, CorrelationId Correlation);

/// <summary>
/// Caso de uso connector-iniciado (AB-4C-001 critérios 6-8): sonda o inventário via
/// <see cref="IEvInventoryAdapter"/> (porta substituível), normaliza em <see cref="InventorySnapshot"/> e
/// decide, ANTES de qualquer escrita, se o resultado é idêntico ao último snapshot persistido (réplay
/// idempotente, nenhuma linha nova — critério 6) ou se representa mudança real (nova versão, evidência
/// anterior nunca reescrita — critério 7). Connector revogado ou inexistente falha fechado.
/// </summary>
public sealed class SubmitInventorySnapshotUseCase(
    IConnectorRegistry connectors, IConnectorInventoryStore inventory, IEvInventoryAdapter adapter, IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorInventoryStore _inventory = inventory;
    private readonly IEvInventoryAdapter _adapter = adapter;
    private readonly IClock _clock = clock;

    /// <summary>Sonda, normaliza e persiste (ou converge por réplay) o inventário do connector autenticado.</summary>
    /// <exception cref="ConnectorNotFoundException">Connector inexistente.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    public async Task<InventorySnapshotAppendResult> ExecuteAsync(
        SubmitInventorySnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identity = await _connectors.GetAsync(request.Scope, request.AuthenticatedConnector, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorNotFoundException();
        if (!identity.IsActive)
        {
            throw new ConnectorRevokedException(identity.Id);
        }

        var probe = await _adapter.ProbeAsync(identity.Id, request.Correlation, cancellationToken).ConfigureAwait(false);

        var latest = await _inventory.GetLatestAsync(request.Scope, identity.Id, cancellationToken).ConfigureAwait(false);

        var now = _clock.UtcNow;
        var candidateVersion = (latest?.Version ?? 0) + 1;
        var candidate = InventorySnapshot.Create(
            InventorySnapshotId.New(), identity.Id, identity.Tenant, identity.Project, candidateVersion,
            probe.Archives, request.Correlation, now);

        if (latest is not null && latest.SnapshotHash == candidate.SnapshotHash)
        {
            // Réplay idempotente: nenhuma mudança real, nenhuma linha nova, nenhuma reexecução de efeito.
            return new InventorySnapshotAppendResult(latest, Created: false);
        }

        return await _inventory.AppendAsync(candidate, cancellationToken).ConfigureAwait(false);
    }
}
