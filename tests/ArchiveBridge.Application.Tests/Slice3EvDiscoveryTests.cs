using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>Slice 3 — adapters por capacidade + avaliação da descoberta (Ready só com evidência; fail-closed).</summary>
public sealed class Slice3EvDiscoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly EvEnvironmentId Env = new(Guid.NewGuid());

    private static EvExportSignature ModernSignature() => EvExportSignature.Create(
        "Export-EVArchive", "Mod", "14", "Cmdlet",
        ["ArchiveId", "OutputPath", "MaxSizeMB", "GenerateReport", "Filter"], ["ArchiveId", "OutputPath"], ["Default"], Now);

    private static EvExportSignature LegacySignature() => EvExportSignature.Create(
        "Export-EVArchive", "Mod", "10", "Cmdlet", ["ArchiveId", "ExportPath"], ["ArchiveId"], ["Default"], Now);

    private static EvDiscoveryObservation Observe(bool cmdlet, bool module, EvExportSignature? signature, bool permissions = true)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", "14.2", "14.2", "PowerShell", Now);
        var probes = new List<EvProbeResult>
        {
            P(EvCapabilityCodes.EvPowershellAvailable, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvModuleAvailable, module ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            P(EvCapabilityCodes.EvSnapinAvailable, CapabilityAvailability.Unavailable),
            P(EvCapabilityCodes.EvDirectoryConnectivity, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvSiteDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvServerDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvVaultStoreDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvArchiveDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvExportCmdletAvailable, cmdlet ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            P(EvCapabilityCodes.EvStagingPathAccess, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvRequiredPermissions, permissions ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
        };
        return new EvDiscoveryObservation(identity, probes, signature, []);
    }

    private static EvProbeResult P(string code, CapabilityAvailability availability) =>
        new(new EvCapabilityCode(code), availability, "ref", availability == CapabilityAvailability.Available ? null : "n/a");

    private static EvAdapterSelection Select(EvDiscoveryObservation observation) =>
        new AdapterCompatibilityEvaluator([new EvExportModernAdapter(), new EvExportLegacyAdapter()]).Select(observation, EvDiscoveryPolicy.Default);

    [Fact]
    public void ModernAdapterRecognizesModernSignatureAndDeclaresCapabilities()
    {
        var evaluation = new EvExportModernAdapter().Evaluate(Observe(cmdlet: true, module: true, ModernSignature()), EvDiscoveryPolicy.Default);
        Assert.Equal(AdapterCompatibility.Supported, evaluation.Compatibility);
        Assert.Contains(evaluation.Capabilities, c => c.CapabilityCode.Value == EvCapabilityCodes.EvExportCmdletSignatureSupported && c.IsAvailable);
        Assert.Contains(evaluation.Capabilities, c => c.CapabilityCode.Value == EvCapabilityCodes.EvExportSizeParameterSupported && c.IsAvailable);
    }

    [Fact]
    public void LegacyAdapterDoesNotRecognizeModernSignature()
    {
        var evaluation = new EvExportLegacyAdapter().Evaluate(Observe(cmdlet: true, module: true, ModernSignature()), EvDiscoveryPolicy.Default);
        Assert.NotEqual(AdapterCompatibility.Supported, evaluation.Compatibility);
    }

    [Fact]
    public void CmdletTextuallyPresentButSignatureIncompatibleIsNotSupported()
    {
        // Cmdlet disponível, mas assinatura sem OutputPath/ExportPath conhecido ⇒ nenhum adapter suporta.
        var alien = EvExportSignature.Create("Export-EVArchive", "Mod", "99", "Cmdlet", ["ArchiveId", "WeirdParam"], ["ArchiveId"], ["Default"], Now);
        var selection = Select(Observe(cmdlet: true, module: true, alien));
        Assert.Equal(AdapterSelectionOutcome.Blocked, selection.Outcome); // reconhece EV (módulo), mas assinatura não suportada
    }

    [Fact]
    public void CmdletMissingButModulePresentIsBlocked() =>
        Assert.Equal(AdapterSelectionOutcome.Blocked, Select(Observe(cmdlet: false, module: true, signature: null)).Outcome);

    [Fact]
    public void NothingRecognizableIsUnsupported() =>
        Assert.Equal(AdapterSelectionOutcome.Unsupported, Select(Observe(cmdlet: false, module: false, signature: null)).Outcome);

    [Fact]
    public void ReadyOnlyWhenAllRequiredCapabilitiesAreAvailable()
    {
        var observation = Observe(cmdlet: true, module: true, ModernSignature());
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Select(observation), EvDiscoveryPolicy.Default, Now, Now);
        Assert.Equal(EvDiscoveryStatus.Ready, result.Status);
        Assert.Equal(EvDiscoveryResultCodes.DiscoveryCompleted, result.ResultCode.Value);
    }

    [Fact]
    public void MissingPermissionBlocksWithPermissionCode()
    {
        var observation = Observe(cmdlet: true, module: true, ModernSignature(), permissions: false);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Select(observation), EvDiscoveryPolicy.Default, Now, Now);
        Assert.Equal(EvDiscoveryStatus.Blocked, result.Status);
        Assert.Equal(EvDiscoveryResultCodes.PermissionInsufficient, result.ResultCode.Value);
    }

    [Fact]
    public void UnsupportedSelectionIsVersionUnsupported()
    {
        var observation = Observe(cmdlet: false, module: false, signature: null);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Select(observation), EvDiscoveryPolicy.Default, Now, Now);
        Assert.Equal(EvDiscoveryStatus.Unsupported, result.Status);
    }

    [Fact]
    public void ValidationRejectsMissingRequiredContext()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var context = new EvDiscoveryCommandContext(1, 1, new Sha256Hash(new string('0', 64)), 1);
        var valid = new EvDiscoveryCommand(scope, Env, "site", "dir", "do", CorrelationId.New(), context);
        EvDiscoveryCommandValidation.EnsureValid(valid);

        Assert.Throws<ArgumentException>(() => EvDiscoveryCommandValidation.EnsureValid(
            valid with { Context = context with { ExpectedConfigurationHash = null } }));
        Assert.Throws<ArgumentException>(() => EvDiscoveryCommandValidation.EnsureValid(valid with { RequestedBy = " " }));
        Assert.Throws<ArgumentException>(() => EvDiscoveryCommandValidation.EnsureValid(
            valid with { Context = context with { SchemaVersion = 99 } }));
    }
}
