using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;

namespace ArchiveBridge.Contracts.EnterpriseVault.Connector;

/// <summary>Resultado bruto de UMA sondagem de inventário — normalizado, sem tipos do fornecedor.</summary>
public sealed record EvInventoryProbeResult(
    string EvVersionDisplay,
    bool PowerShellSnapinAvailable,
    IReadOnlyList<InventoryArchiveRecord> Archives);

/// <summary>
/// Porta substituível de inventário read-only, executada perto do dado (nunca no Control Plane, §15/§16.2,
/// AB-4C-001 item 6). Este Passo NÃO exige uma implementação real de <c>Get-EVArchive</c> — apenas o
/// contrato e um adapter de teste/fixture determinístico (ver Infrastructure). Nenhuma implementação desta
/// porta pode ter efeito colateral (somente leitura/metadados, docs/ev/capability-discovery.md regra 2).
/// </summary>
public interface IEvInventoryAdapter
{
    /// <summary>Sonda o inventário atual do connector — somente leitura, sem efeito colateral.</summary>
    Task<EvInventoryProbeResult> ProbeAsync(ConnectorId connector, CorrelationId correlation, CancellationToken cancellationToken);
}
