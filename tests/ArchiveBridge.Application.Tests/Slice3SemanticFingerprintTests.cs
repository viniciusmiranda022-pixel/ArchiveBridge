using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Slice 3 — completude e determinismo do <see cref="EvDiscoverySemanticFingerprint"/>: qualquer alteração
/// em um campo que muda o SIGNIFICADO da avaliação (maturidade, compatibilidade, precedência, perfil,
/// requisito, achado, categoria de erro, desfecho da seleção, candidatos) altera o hash; e o mesmo conteúdo
/// semântico em ORDEM diferente produz o MESMO hash (ordenação canônica ordinal).
/// </summary>
public sealed class Slice3SemanticFingerprintTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly EvEnvironmentId Env = new(Guid.NewGuid());

    private static EvCapability Cap(string code = EvCapabilityCodes.EvExportCmdletAvailable, CapabilityAvailability availability = CapabilityAvailability.Available) =>
        new(new EvCapabilityCode(code), 1, availability, "ref", availability == CapabilityAvailability.Available ? null : "n/a", Now);

    private static EvAdapterRequirement Req(string code = EvCapabilityCodes.EvExportCmdletAvailable, string description = "req") =>
        new(new EvCapabilityCode(code), description);

    private static EvDiscoveryFinding Finding(
        string code = EvDiscoveryResultCodes.CapabilityIndeterminate, string? capabilityCode = null,
        string reason = "r", EvErrorCategory category = EvErrorCategory.None) =>
        new(new EvDiscoveryResultCode(code), capabilityCode is null ? null : new EvCapabilityCode(capabilityCode), reason, Now, category);

    private static EvExportMaturity Maturity(
        bool runtime = true, bool official = true, bool fixtures = true, bool lab = false) =>
        new(runtime, official, fixtures, lab);

    private static EvAdapterEvaluation Eval(
        string adapterId = "adapter-a", int version = 1, AdapterCompatibility compatibility = AdapterCompatibility.Supported,
        int precedence = 20, string? profileId = "PROFILE", EvExportMaturity? maturity = null,
        EvAdapterRequirement[]? requirements = null, EvDiscoveryFinding[]? findings = null, EvCapability[]? capabilities = null) =>
        new(new EvAdapterId(adapterId), version, compatibility, capabilities ?? [Cap()], requirements ?? [Req()], findings ?? [], precedence,
            profileId, maturity ?? Maturity());

    private static Sha256Hash Hash(
        EvAdapterEvaluation[] evaluations, AdapterSelectionOutcome outcome = AdapterSelectionOutcome.Supported,
        EvAdapterId[]? candidates = null, EvDiscoveryFinding[]? selectionFindings = null, EvDiscoveryFinding[]? blockingFindings = null,
        EvExportSignature? signature = null, EvDiscoveryStatus status = EvDiscoveryStatus.Ready,
        string resultCode = EvDiscoveryResultCodes.DiscoveryCompleted)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", "15.1", "15.1", "PowerShell", Now);
        var capabilitySet = EvCapabilitySet.Create(Env, new EvAdapterId("adapter-a"), 1, EvDiscoverySchema.Version, [Cap()], status);
        var selection = new EvAdapterSelection(
            outcome,
            evaluations.FirstOrDefault(e => e.Compatibility == AdapterCompatibility.Supported),
            candidates ?? [.. evaluations.Select(static e => e.AdapterId)],
            evaluations,
            selectionFindings ?? []);
        var result = new EvDiscoveryRunResult(
            DiscoveryRunId.New(), identity, capabilitySet, selection, signature, blockingFindings ?? [], status,
            new EvDiscoveryResultCode(resultCode), Now, Now);
        return EvDiscoverySemanticFingerprint.Compute(result);
    }

    private static readonly Sha256Hash Baseline = Hash([Eval()]);

    [Fact]
    public void FingerprintIsDeterministicForIdenticalInput() =>
        Assert.Equal(Baseline.Value, Hash([Eval()]).Value);

    [Fact]
    public void FingerprintChangesWithMaturityRuntimeObserved() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(maturity: Maturity(runtime: false))]).Value);

    [Fact]
    public void FingerprintChangesWithMaturityOfficialDocumentation() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(maturity: Maturity(official: false))]).Value);

    [Fact]
    public void FingerprintChangesWithMaturityAutomatedFixtureValidated() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(maturity: Maturity(fixtures: false))]).Value);

    [Fact]
    public void FingerprintChangesWithMaturityLaboratoryValidated() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(maturity: Maturity(lab: true))]).Value);

    [Fact]
    public void FingerprintChangesWithCompatibility() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(compatibility: AdapterCompatibility.Blocked)], outcome: AdapterSelectionOutcome.Blocked).Value);

    [Fact]
    public void FingerprintChangesWithPrecedence() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(precedence: 21)]).Value);

    [Fact]
    public void FingerprintChangesWithProfileId() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(profileId: "OTHER")]).Value);

    [Fact]
    public void FingerprintChangesWithAdapterRequirement() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(requirements: [Req(description: "outro requisito")])]).Value);

    [Fact]
    public void FingerprintChangesWithAdapterFinding() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(findings: [Finding(reason: "achado do adapter")])]).Value);

    [Fact]
    public void FingerprintChangesWithFindingErrorCategory()
    {
        var a = Hash([Eval(findings: [Finding(category: EvErrorCategory.None)])]);
        var b = Hash([Eval(findings: [Finding(category: EvErrorCategory.ExecutionFailed)])]);
        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public void FingerprintChangesWithSelectionOutcome() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval()], outcome: AdapterSelectionOutcome.Ambiguous).Value);

    [Fact]
    public void FingerprintChangesWithAdapterCandidates() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval()], candidates: [new EvAdapterId("adapter-a"), new EvAdapterId("adapter-z")]).Value);

    [Fact]
    public void FingerprintChangesWithSelectionFindings() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval()], selectionFindings: [Finding(reason: "achado da seleção")]).Value);

    [Fact]
    public void FingerprintChangesWithDeclaredCapabilities() =>
        Assert.NotEqual(Baseline.Value, Hash([Eval(capabilities: [Cap(), Cap(EvCapabilityCodes.EvExportPstSupported)])]).Value);

    [Fact]
    public void FingerprintIsIndependentOfEvaluationOrder()
    {
        var evalA = Eval("adapter-a", precedence: 10, compatibility: AdapterCompatibility.Blocked);
        var evalB = Eval("adapter-b", precedence: 20);
        Assert.Equal(
            Hash([evalA, evalB]).Value,
            Hash([evalB, evalA]).Value);
    }

    [Fact]
    public void FingerprintIsIndependentOfRequirementFindingAndCapabilityOrder()
    {
        var forward = Eval(
            requirements: [Req(EvCapabilityCodes.EvExportCmdletAvailable), Req(EvCapabilityCodes.EvExportPstSupported)],
            findings: [Finding(EvDiscoveryResultCodes.CmdletNotAvailable), Finding(EvDiscoveryResultCodes.PermissionInsufficient)],
            capabilities: [Cap(EvCapabilityCodes.EvExportCmdletAvailable), Cap(EvCapabilityCodes.EvExportPstSupported)]);
        var reversed = Eval(
            requirements: [Req(EvCapabilityCodes.EvExportPstSupported), Req(EvCapabilityCodes.EvExportCmdletAvailable)],
            findings: [Finding(EvDiscoveryResultCodes.PermissionInsufficient), Finding(EvDiscoveryResultCodes.CmdletNotAvailable)],
            capabilities: [Cap(EvCapabilityCodes.EvExportPstSupported), Cap(EvCapabilityCodes.EvExportCmdletAvailable)]);
        Assert.Equal(Hash([forward]).Value, Hash([reversed]).Value);
    }

    [Fact]
    public void FingerprintIsIndependentOfCandidateOrder() =>
        Assert.Equal(
            Hash([Eval()], candidates: [new EvAdapterId("adapter-a"), new EvAdapterId("adapter-b")]).Value,
            Hash([Eval()], candidates: [new EvAdapterId("adapter-b"), new EvAdapterId("adapter-a")]).Value);
}
