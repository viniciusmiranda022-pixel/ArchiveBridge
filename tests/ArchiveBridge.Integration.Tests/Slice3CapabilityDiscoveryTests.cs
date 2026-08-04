using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 3 — descoberta READ-ONLY de capacidades via sondas tipadas (sem Enterprise Vault real, host de
/// fixture). Cada sonda produz seu PRÓPRIO resultado: a falha ou permissão negada de uma sonda não colapsa
/// as demais; contagem negativa/ausente vira Indeterminate com achado de saída inválida; a permissão negada
/// é derivada por sonda; a assinatura só é lida quando o cmdlet está disponível.
/// </summary>
public sealed class Slice3CapabilityDiscoveryTests
{
    private static readonly EvDiscoveryPolicy Policy = EvDiscoveryPolicy.Default;

    private static async Task<EvDiscoveryObservation> ProbeAsync(FixtureEvPowerShellHost host)
    {
        var clock = new MutableClock(Slice2Support.Now);
        return await Slice3Support.Discovery(host, clock).ProbeAsync(Slice3Support.NewEnvironment(), Policy, CancellationToken.None);
    }

    private static CapabilityAvailability Of(EvDiscoveryObservation observation, string code) => observation.AvailabilityOf(code);

    [Fact]
    public async Task ReadyEnvironmentYieldsFactualCapabilitiesAndOfficialSignature()
    {
        var observation = await ProbeAsync(Slice3Support.ReadyHost());

        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvPowershellAvailable));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvSiteDiscovery));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvArchiveDiscovery));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvExportCmdletAvailable));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvRequiredPermissions));
        Assert.Empty(observation.Findings);

        Assert.NotNull(observation.ExportSignature);
        Assert.True(observation.ExportSignature!.HasParameter(EvExportParameters.ArchiveId));
        Assert.True(observation.ExportSignature.HasParameter(EvExportParameters.OutputDirectory));
        Assert.True(observation.ExportSignature.HasParameter(EvExportParameters.Format));
        Assert.True(observation.ExportSignature.HasParameter(EvExportParameters.MaxPstSizeMb));
    }

    [Fact]
    public async Task PermissionDeniedOnArchiveIsPerProbeAndDoesNotCollapseOthers()
    {
        var observation = await ProbeAsync(Slice3Support.ArchivePermissionDeniedHost());

        // Somente a sonda de archives é barrada por permissão; as demais mantêm seus próprios resultados.
        Assert.Equal(CapabilityAvailability.PermissionDenied, Of(observation, EvCapabilityCodes.EvArchiveDiscovery));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvSiteDiscovery));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvServerDiscovery));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvVaultStoreDiscovery));

        // A permissão exigida (derivada) é negada porque UMA sonda foi barrada.
        Assert.Equal(CapabilityAvailability.PermissionDenied, Of(observation, EvCapabilityCodes.EvRequiredPermissions));

        var finding = Assert.Single(observation.Findings, f => f.CapabilityCode?.Value == EvCapabilityCodes.EvArchiveDiscovery);
        Assert.Equal(EvErrorCategory.PermissionDenied, finding.ErrorCategory);
    }

    [Fact]
    public async Task NegativeCountYieldsIndeterminateWithOutputInvalidFinding()
    {
        var host = Slice3Support.ReadyHost();
        host.Set(EvPowerShellProbeKind.EvSite, FixtureEvPowerShellHost.SuccessEnvelope("EvSite", "{\"count\":-1}"));
        var observation = await ProbeAsync(host);

        Assert.Equal(CapabilityAvailability.Indeterminate, Of(observation, EvCapabilityCodes.EvSiteDiscovery));
        var finding = Assert.Single(observation.Findings, f => f.CapabilityCode?.Value == EvCapabilityCodes.EvSiteDiscovery);
        Assert.Equal(EvErrorCategory.OutputInvalid, finding.ErrorCategory);
        Assert.Equal(EvDiscoveryResultCodes.DiscoveryOutputInvalid, finding.ResultCode.Value);

        // Outras sondas seguem íntegras; sem permissão negada global.
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvArchiveDiscovery));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvRequiredPermissions));
    }

    [Fact]
    public async Task CmdletMetadataUnavailableLeavesSignatureNullButKeepsOtherProbes()
    {
        var host = Slice3Support.ReadyHost();
        host.Set(EvPowerShellProbeKind.ExportEvArchiveCommandMetadata, FixtureEvPowerShellHost.FailureEnvelope("ExportEvArchiveCommandMetadata", "NOT_AVAILABLE"));
        var observation = await ProbeAsync(host);

        Assert.Null(observation.ExportSignature);
        Assert.Equal(CapabilityAvailability.Indeterminate, Of(observation, EvCapabilityCodes.EvExportCmdletAvailable));
        // Falha do metadado NÃO é permissão negada: a permissão derivada continua disponível.
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvRequiredPermissions));
        Assert.Equal(CapabilityAvailability.Available, Of(observation, EvCapabilityCodes.EvSiteDiscovery));
    }
}
