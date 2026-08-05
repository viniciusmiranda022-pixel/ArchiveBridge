using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Slice 3 — adapter do perfil DOCUMENTADO 15.1 + avaliação da descoberta. A assinatura oficial do
/// <c>Export-EVArchive</c> é ArchiveId/OutputDirectory/SearchString/Format/MaxThreads/Retry/MaxPSTSizeMB;
/// não há GenerateReport (relatório automático). Ready só com evidência; fail-closed.
/// </summary>
public sealed class Slice3EvDiscoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly EvEnvironmentId Env = new(Guid.NewGuid());

    // Assinatura OFICIAL 15.1 completa (sem GenerateReport).
    private static EvExportSignature OfficialSignature(IEnumerable<string>? parameters = null) => EvExportSignature.Create(
        "Export-EVArchive", "Symantec.EnterpriseVault.PowerShell", "15.1", "Cmdlet",
        parameters ?? ["ArchiveId", "OutputDirectory", "SearchString", "Format", "MaxThreads", "Retry", "MaxPSTSizeMB"],
        ["ArchiveId", "OutputDirectory"], ["Default"], Now);

    private static EvDiscoveryObservation Observe(bool cmdlet, bool module, EvExportSignature? signature, bool permissions = true)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", "15.1", "15.1", "PowerShell", Now);
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
        new AdapterCompatibilityEvaluator([new EvExportDocumented151Adapter()]).Select(observation, EvDiscoveryPolicy.Default);

    private static bool Cap(EvAdapterEvaluation e, string code, CapabilityAvailability availability) =>
        e.Capabilities.Any(c => c.CapabilityCode.Value == code && c.Availability == availability);

    [Fact]
    public void OfficialSignatureIsRecognizedWithDocumentedProfileAndMaturity()
    {
        var evaluation = new EvExportDocumented151Adapter().Evaluate(Observe(true, true, OfficialSignature()), EvDiscoveryPolicy.Default);
        Assert.Equal(AdapterCompatibility.Supported, evaluation.Compatibility);
        Assert.Equal(EvExportProfile.Documented151Id, evaluation.ProfileId);
        Assert.True(evaluation.Maturity!.RuntimeObserved);
        Assert.True(evaluation.Maturity.OfficialDocumentation);
        Assert.False(evaluation.Maturity.LaboratoryValidated); // nunca homologado sem laboratório
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportCmdletSignatureSupported, CapabilityAvailability.Available));
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportPstSupported, CapabilityAvailability.Available));
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportSizeParameterSupported, CapabilityAvailability.Available));
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportFilteringSupported, CapabilityAvailability.Available));
    }

    [Fact]
    public void ReportSupportIsIndependentOfGenerateReportParameter()
    {
        // A assinatura oficial NÃO tem GenerateReport, mas o relatório é gerado automaticamente:
        // EvExportReportSupported deve ser Available quando a exportação é suportada.
        var evaluation = new EvExportDocumented151Adapter().Evaluate(Observe(true, true, OfficialSignature()), EvDiscoveryPolicy.Default);
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportReportSupported, CapabilityAvailability.Available));
    }

    [Theory]
    [InlineData("ArchiveId")]
    [InlineData("OutputDirectory")]
    [InlineData("Format")]
    public void MissingIdentifyingParameterIsNotRecognized(string missing)
    {
        var parameters = new List<string> { "ArchiveId", "OutputDirectory", "SearchString", "Format", "MaxThreads", "Retry", "MaxPSTSizeMB" };
        parameters.Remove(missing);
        var selection = Select(Observe(true, true, OfficialSignature(parameters)));
        Assert.Equal(AdapterSelectionOutcome.Blocked, selection.Outcome); // reconhece EV (módulo) mas assinatura não documentada
    }

    [Fact]
    public void MissingMaxPstSizeStillSupportedButSizeCapabilityUnavailable()
    {
        var parameters = new[] { "ArchiveId", "OutputDirectory", "SearchString", "Format", "MaxThreads", "Retry" };
        var evaluation = new EvExportDocumented151Adapter().Evaluate(Observe(true, true, OfficialSignature(parameters)), EvDiscoveryPolicy.Default);
        Assert.Equal(AdapterCompatibility.Supported, evaluation.Compatibility);
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportSizeParameterSupported, CapabilityAvailability.Unavailable));
    }

    [Fact]
    public void MissingSearchStringStillSupportedButFilteringUnavailable()
    {
        var parameters = new[] { "ArchiveId", "OutputDirectory", "Format", "MaxThreads", "Retry", "MaxPSTSizeMB" };
        var evaluation = new EvExportDocumented151Adapter().Evaluate(Observe(true, true, OfficialSignature(parameters)), EvDiscoveryPolicy.Default);
        Assert.Equal(AdapterCompatibility.Supported, evaluation.Compatibility);
        Assert.True(Cap(evaluation, EvCapabilityCodes.EvExportFilteringSupported, CapabilityAvailability.Unavailable));
    }

    [Fact]
    public void UnknownExtraParameterDoesNotBreakRecognition()
    {
        var parameters = new[] { "ArchiveId", "OutputDirectory", "Format", "MaxPSTSizeMB", "SomeFutureUnknownParam" };
        Assert.Equal(AdapterSelectionOutcome.Supported, Select(Observe(true, true, OfficialSignature(parameters))).Outcome);
    }

    [Fact]
    public void ParameterNamesAreCaseInsensitive()
    {
        var parameters = new[] { "archiveid", "outputdirectory", "format", "maxpstsizemb" };
        Assert.Equal(AdapterSelectionOutcome.Supported, Select(Observe(true, true, OfficialSignature(parameters))).Outcome);
    }

    [Fact]
    public void CmdletMissingButModulePresentIsBlocked() =>
        Assert.Equal(AdapterSelectionOutcome.Blocked, Select(Observe(false, true, signature: null)).Outcome);

    [Fact]
    public void NothingRecognizableIsUnsupported() =>
        Assert.Equal(AdapterSelectionOutcome.Unsupported, Select(Observe(false, false, signature: null)).Outcome);

    [Fact]
    public void ReadyOnlyWhenAllRequiredCapabilitiesAreAvailable()
    {
        var observation = Observe(true, true, OfficialSignature());
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Select(observation), EvDiscoveryPolicy.Default, Now, Now);
        Assert.Equal(EvDiscoveryStatus.Ready, result.Status);
        Assert.Equal(EvDiscoveryResultCodes.DiscoveryCompleted, result.ResultCode.Value);
    }

    [Fact]
    public void MissingPermissionBlocksWithPermissionCode()
    {
        var observation = Observe(true, true, OfficialSignature(), permissions: false);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Select(observation), EvDiscoveryPolicy.Default, Now, Now);
        Assert.Equal(EvDiscoveryStatus.Blocked, result.Status);
        Assert.Equal(EvDiscoveryResultCodes.PermissionInsufficient, result.ResultCode.Value);
    }

    [Fact]
    public void UnsupportedSelectionIsVersionUnsupported()
    {
        var observation = Observe(false, false, signature: null);
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
