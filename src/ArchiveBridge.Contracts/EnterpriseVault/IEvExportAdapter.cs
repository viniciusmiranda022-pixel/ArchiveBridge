using ArchiveBridge.Domain.EnterpriseVault;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.EnterpriseVault;

/// <summary>
/// Capability de exportação descoberta para um archive, normalizada — sem versões nem tipos
/// do Enterprise Vault. Versão do EV não implica suporte (ADR-0013): quem decide é a capability.
/// </summary>
public readonly record struct EvExportCapability(bool SupportsMultiVersion, bool SupportsServerSideSplit);

/// <summary>
/// Porta de exportação EV multiversão por capability discovery (ADR-0013). Os tipos do
/// Enterprise Vault (COM/PowerShell) nunca cruzam esta fronteira: o domínio recebe apenas
/// descritores normalizados de parts.
/// </summary>
public interface IEvExportAdapter
{
    /// <summary>Descobre a capability de exportação do archive sem assumir versão do EV.</summary>
    Task<EvExportCapability> DiscoverAsync(SourceArchiveId archive, CancellationToken cancellationToken);

    /// <summary>
    /// Solicita a exportação segmentada na origem e devolve os identificadores normalizados das
    /// parts produzidas. Não há tipos do fornecedor no retorno.
    /// </summary>
    Task<IReadOnlyList<ArtifactId>> ExportAsync(SourceArchiveId archive, CancellationToken cancellationToken);
}
