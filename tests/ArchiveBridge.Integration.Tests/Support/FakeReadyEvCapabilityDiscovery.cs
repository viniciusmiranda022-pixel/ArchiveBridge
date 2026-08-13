using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>
/// Fake test-only de <see cref="IEvCapabilityDiscovery"/> — usado apenas nos testes. Devolve uma
/// <see cref="EvDiscoveryObservation"/> DETERMINÍSTICA de um ambiente Enterprise Vault 15.1 "pronto" (todas
/// as capacidades exigidas Available + assinatura oficial do <c>Export-EVArchive</c>), compatível com os
/// contratos do Slice 3, SEM Enterprise Vault real, SEM PowerShell e SEM qualquer processo externo. Permite
/// o E2E exercer o pipeline durável REAL (SQL, Jobs, Inbox, Processor, EvidenceStore) sem tocar em EV. Nunca
/// executa <c>Export-EVArchive</c> — apenas descreve, como observação, os metadados do cmdlet.
/// </summary>
internal sealed class FakeReadyEvCapabilityDiscovery(DateTimeOffset observedAtUtc) : IEvCapabilityDiscovery
{
    private readonly DateTimeOffset _observedAtUtc = observedAtUtc;

    public Task<EvDiscoveryObservation> ProbeAsync(
        EvEnvironmentDescriptor environment, EvDiscoveryPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var identity = new EvEnvironmentIdentity(
            environment.EnvironmentId, environment.SiteName, environment.DirectoryServer,
            "15.1", "15.1", "PowerShell", _observedAtUtc);

        var probes = new List<EvProbeResult>
        {
            Probe(EvCapabilityCodes.EvPowershellAvailable),
            Probe(EvCapabilityCodes.EvModuleAvailable),
            Probe(EvCapabilityCodes.EvSnapinAvailable, CapabilityAvailability.Unavailable),
            Probe(EvCapabilityCodes.EvDirectoryConnectivity),
            Probe(EvCapabilityCodes.EvSiteDiscovery),
            Probe(EvCapabilityCodes.EvServerDiscovery),
            Probe(EvCapabilityCodes.EvVaultStoreDiscovery),
            Probe(EvCapabilityCodes.EvArchiveDiscovery),
            Probe(EvCapabilityCodes.EvExportCmdletAvailable),
            Probe(EvCapabilityCodes.EvStagingPathAccess),
            Probe(EvCapabilityCodes.EvRequiredPermissions),
        };

        var signature = EvExportSignature.Create(
            "Export-EVArchive", "Symantec.EnterpriseVault.PowerShell", "15.1", "Cmdlet",
            ["ArchiveId", "OutputDirectory", "SearchString", "Format", "MaxThreads", "Retry", "MaxPSTSizeMB"],
            ["ArchiveId", "OutputDirectory"], ["Default"], _observedAtUtc);

        return Task.FromResult(new EvDiscoveryObservation(identity, probes, signature, []));
    }

    private static EvProbeResult Probe(string code, CapabilityAvailability availability = CapabilityAvailability.Available) =>
        new(new EvCapabilityCode(code), availability, "ref", availability == CapabilityAvailability.Available ? null : "n/a");
}
