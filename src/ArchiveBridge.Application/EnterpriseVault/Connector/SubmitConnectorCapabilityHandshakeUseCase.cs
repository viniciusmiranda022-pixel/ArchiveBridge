using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Application.EnterpriseVault.Connector;

/// <summary>
/// Solicitação de handshake — <see cref="Scope"/> e <see cref="AuthenticatedConnector"/> são resolvidos
/// juntos pelo composition root a partir do transporte autenticado do connector (mesmo padrão de
/// <c>IPortalScopeAccessor</c> para principals do Portal); nunca informados livremente pelo chamador.
/// </summary>
public sealed record SubmitConnectorCapabilityHandshakeRequest(
    TenantScope Scope,
    ConnectorId AuthenticatedConnector,
    string EvVersionDisplay,
    bool PowerShellSnapinAvailable,
    CorrelationId Correlation);

/// <summary>
/// Caso de uso connector-iniciado (AB-4C-001 critérios 4/5): registra um novo handshake de
/// capability/versão, sempre derivando <see cref="ConnectorCapabilityHandshake.ExportCapable"/> no Domain
/// a partir da matriz de suporte — nunca aceito como entrada. Connector revogado ou inexistente falha
/// fechado.
/// </summary>
public sealed class SubmitConnectorCapabilityHandshakeUseCase(
    IConnectorRegistry connectors, IConnectorCapabilityStore capabilities, IClock clock)
{
    private readonly IConnectorRegistry _connectors = connectors;
    private readonly IConnectorCapabilityStore _capabilities = capabilities;
    private readonly IClock _clock = clock;

    /// <summary>Avalia e persiste um novo handshake de capability/versão para o connector autenticado.</summary>
    /// <exception cref="ConnectorNotFoundException">Connector inexistente.</exception>
    /// <exception cref="ConnectorRevokedException">Connector revogado.</exception>
    public async Task<ConnectorCapabilityHandshake> ExecuteAsync(
        SubmitConnectorCapabilityHandshakeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identity = await _connectors.GetAsync(request.Scope, request.AuthenticatedConnector, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorNotFoundException();
        if (!identity.IsActive)
        {
            throw new ConnectorRevokedException(identity.Id);
        }

        var handshake = ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), identity.Id, identity.Tenant, identity.Project,
            request.EvVersionDisplay, request.PowerShellSnapinAvailable, request.Correlation, _clock.UtcNow);

        await _capabilities.AppendAsync(handshake, cancellationToken).ConfigureAwait(false);
        return handshake;
    }
}
