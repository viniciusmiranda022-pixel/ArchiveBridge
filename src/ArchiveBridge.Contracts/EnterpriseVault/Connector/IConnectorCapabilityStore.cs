using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Contracts.EnterpriseVault.Connector;

/// <summary>
/// Store append-only de handshakes de capability/versão do connector. Cada envio é um novo registro
/// imutável — evidência anterior nunca é reescrita; o handshake vigente é sempre o mais recente por
/// <see cref="ConnectorCapabilityHandshake.CollectedAtUtc"/> (ver <see cref="GetLatestAsync"/>).
/// </summary>
public interface IConnectorCapabilityStore
{
    /// <summary>Persiste um novo handshake (append).</summary>
    Task AppendAsync(ConnectorCapabilityHandshake handshake, CancellationToken cancellationToken);

    /// <summary>Devolve o handshake vigente (mais recente) dentro do escopo; <see langword="null"/> se nenhum existir.</summary>
    Task<ConnectorCapabilityHandshake?> GetLatestAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken);
}
