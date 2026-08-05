using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
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

    // ---- Falha mecânica nunca resulta em Ready (avaliação completa) --------------------------------------

    private static async Task<EvDiscoveryRunResult> EvaluateAsync(FixtureEvPowerShellHost host)
    {
        var clock = new MutableClock(Slice2Support.Now);
        var env = Slice3Support.NewEnvironment();
        var observation = await Slice3Support.Discovery(host, clock).ProbeAsync(env, Policy, CancellationToken.None);
        var selection = Slice3Support.Adapters().Select(observation, Policy);
        return EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, selection, Policy, clock.UtcNow, clock.UtcNow);
    }

    [Fact]
    public async Task CleanEnvironmentReachesReady() =>
        Assert.Equal(EvDiscoveryStatus.Ready, (await EvaluateAsync(Slice3Support.ReadyHost())).Status);

    [Fact]
    public async Task NonZeroExitOnNonRequiredCapabilityForcesFailedNotReady()
    {
        var host = Slice3Support.ReadyHost();
        // EvServerDiscovery NÃO está em RequiredForReady; mesmo assim, ExitCode != 0 é falha MECÂNICA ⇒ Failed.
        host.SetExecution(EvPowerShellProbeKind.EvServer, new EvProbeExecution(1, string.Empty, "boom", false, false));
        Assert.Equal(EvDiscoveryStatus.Failed, (await EvaluateAsync(host)).Status);
    }

    [Fact]
    public async Task NonEmptyStderrOnNonRequiredCapabilityForcesFailed()
    {
        var host = Slice3Support.ReadyHost();
        host.SetExecution(EvPowerShellProbeKind.EvVaultStore, new EvProbeExecution(0, "{\"count\":1}", "CategoryInfo: erro", false, false));
        Assert.Equal(EvDiscoveryStatus.Failed, (await EvaluateAsync(host)).Status);
    }

    [Fact]
    public async Task TimeoutOnAnyProbeForcesFailed()
    {
        var host = Slice3Support.ReadyHost();
        host.SetExecution(EvPowerShellProbeKind.EvServer, new EvProbeExecution(0, string.Empty, string.Empty, TimedOut: true, false));
        Assert.Equal(EvDiscoveryStatus.Failed, (await EvaluateAsync(host)).Status);
    }

    [Fact]
    public async Task OutputInvalidOnAnyProbeForcesFailed()
    {
        var host = Slice3Support.ReadyHost();
        host.Set(EvPowerShellProbeKind.EvServer, FixtureEvPowerShellHost.SuccessEnvelope("EvServer", "{\"count\":-1}"));
        Assert.Equal(EvDiscoveryStatus.Failed, (await EvaluateAsync(host)).Status);
    }

    [Fact]
    public async Task PermissionDeniedStaysBlockedNotFailed()
    {
        var result = await EvaluateAsync(Slice3Support.ArchivePermissionDeniedHost());
        Assert.Equal(EvDiscoveryStatus.Blocked, result.Status);
        Assert.Equal(EvDiscoveryResultCodes.PermissionInsufficient, result.ResultCode.Value);
    }
}
