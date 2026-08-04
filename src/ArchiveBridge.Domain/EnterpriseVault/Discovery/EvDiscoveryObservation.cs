namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Resultado bruto de uma sonda read-only para uma capacidade: a disponibilidade OBSERVADA com a
/// referência de evidência. Uma sonda que não comprova presença resulta em
/// <see cref="CapabilityAvailability.Indeterminate"/> (ou Unavailable/PermissionDenied), nunca Available.
/// </summary>
public sealed record EvProbeResult(
    EvCapabilityCode CapabilityCode,
    CapabilityAvailability Availability,
    string EvidenceReference,
    string? BlockingReason);

/// <summary>
/// Observação FACTUAL de um ambiente Enterprise Vault produzida por uma execução de descoberta read-only:
/// a identidade observada, os resultados de sonda por capacidade, a assinatura observada do cmdlet de
/// exportação (se presente) e os achados de nível de sonda. Não julga suporte de versão — a
/// interpretação por capacidades é feita pelos adapters. Não contém PII de mensagens/PST.
/// </summary>
public sealed record EvDiscoveryObservation(
    EvEnvironmentIdentity Environment,
    IReadOnlyList<EvProbeResult> ProbeResults,
    EvExportSignature? ExportSignature,
    IReadOnlyList<EvDiscoveryFinding> Findings)
{
    /// <summary>Disponibilidade observada de uma capacidade (Indeterminate se a sonda não a cobriu).</summary>
    public CapabilityAvailability AvailabilityOf(string capabilityCode)
    {
        foreach (var probe in ProbeResults)
        {
            if (string.Equals(probe.CapabilityCode.Value, capabilityCode, StringComparison.Ordinal))
            {
                return probe.Availability;
            }
        }

        return CapabilityAvailability.Indeterminate;
    }

    /// <summary>Indica se a sonda comprovou (evidência positiva) a capacidade indicada.</summary>
    public bool Observed(string capabilityCode) => AvailabilityOf(capabilityCode) == CapabilityAvailability.Available;
}
