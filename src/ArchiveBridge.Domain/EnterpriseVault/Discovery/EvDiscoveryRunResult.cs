namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Resultado avaliado de uma execução de descoberta (antes da persistência): a identidade observada, o
/// conjunto de capacidades, a seleção de adapter, os achados de bloqueio, o estado terminal e o código
/// de resultado estruturado. É a partir daqui que a evidência imutável e os metadados SQL são derivados.
/// </summary>
public sealed record EvDiscoveryRunResult(
    DiscoveryRunId RunId,
    EvEnvironmentIdentity Environment,
    EvCapabilitySet CapabilitySet,
    EvAdapterSelection Selection,
    EvExportSignature? Signature,
    IReadOnlyList<EvDiscoveryFinding> BlockingFindings,
    EvDiscoveryStatus Status,
    EvDiscoveryResultCode ResultCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
