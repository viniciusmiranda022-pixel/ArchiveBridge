using System.Text.Json;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 3 — artefato de evidência (<c>evidence.json</c>): o <see cref="EvDiscoveryEvidenceSerializer"/>
/// produz bytes canônicos cujo <c>ContentSha256</c> muda ao alterar QUALQUER campo semântico (maturidade,
/// compatibilidade, precedência, perfil, requisito, achado, categoria de erro, desfecho da seleção,
/// candidatos); a mesma avaliação em ORDEM diferente produz bytes byte-a-byte idênticos; e o documento
/// registra avaliações completas + maturidade + categorias de erro + as impressões digitais AUTORITATIVAS,
/// permitindo a um auditor determinar a seleção sem depender do SQL.
/// </summary>
public sealed class Slice3EvidenceArtifactTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly EvEnvironmentId Env = new(Guid.NewGuid());
    private static readonly Sha256Hash ConfigHash = new(new string('c', 64));
    private static readonly EvDiscoveryEvidenceSerializer Serializer = new();

    private static EvCapability Cap(string code = EvCapabilityCodes.EvExportCmdletAvailable, CapabilityAvailability availability = CapabilityAvailability.Available) =>
        new(new EvCapabilityCode(code), 1, availability, "ref", availability == CapabilityAvailability.Available ? null : "n/a", Now);

    private static EvAdapterRequirement Req(string code = EvCapabilityCodes.EvExportCmdletAvailable, string description = "req") =>
        new(new EvCapabilityCode(code), description);

    private static EvDiscoveryFinding Finding(
        string code = EvDiscoveryResultCodes.CapabilityIndeterminate, string? capabilityCode = null,
        string reason = "r", EvErrorCategory category = EvErrorCategory.None) =>
        new(new EvDiscoveryResultCode(code), capabilityCode is null ? null : new EvCapabilityCode(capabilityCode), reason, Now, category);

    private static EvAdapterEvaluation Eval(
        string adapterId = "adapter-a", int precedence = 20, string? profileId = "PROFILE",
        AdapterCompatibility compatibility = AdapterCompatibility.Supported, EvExportMaturity? maturity = null,
        EvAdapterRequirement[]? requirements = null, EvDiscoveryFinding[]? findings = null, EvCapability[]? capabilities = null) =>
        new(new EvAdapterId(adapterId), 1, compatibility, capabilities ?? [Cap()], requirements ?? [Req()], findings ?? [], precedence,
            profileId, maturity ?? new EvExportMaturity(true, true, true, false));

    private static EvDiscoveryEvidenceBytes Serialize(
        EvAdapterEvaluation[] evaluations, AdapterSelectionOutcome outcome = AdapterSelectionOutcome.Supported,
        EvAdapterId[]? candidates = null, EvDiscoveryStatus status = EvDiscoveryStatus.Ready)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", "15.1", "15.1", "PowerShell", Now);
        var capabilitySet = EvCapabilitySet.Create(Env, new EvAdapterId("adapter-a"), 1, EvDiscoverySchema.Version, [Cap()], status);
        var selection = new EvAdapterSelection(
            outcome, evaluations.FirstOrDefault(static e => e.Compatibility == AdapterCompatibility.Supported),
            candidates ?? [.. evaluations.Select(static e => e.AdapterId)], evaluations, []);
        var result = new EvDiscoveryRunResult(
            DiscoveryRunId.New(), identity, capabilitySet, selection, Signature: null, [], status,
            new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryCompleted), Now, Now);
        return Serializer.Serialize(result, ConfigHash, EvDiscoverySemanticFingerprint.Compute(result));
    }

    private static string Sha(EvAdapterEvaluation[] evaluations, AdapterSelectionOutcome outcome = AdapterSelectionOutcome.Supported, EvAdapterId[]? candidates = null) =>
        Serialize(evaluations, outcome, candidates).ContentSha256.Value;

    private static readonly string BaselineSha = Sha([Eval()]);

    [Fact]
    public void ContentShaChangesWithMaturityFlags()
    {
        Assert.NotEqual(BaselineSha, Sha([Eval(maturity: new EvExportMaturity(false, true, true, false))]));
        Assert.NotEqual(BaselineSha, Sha([Eval(maturity: new EvExportMaturity(true, false, true, false))]));
        Assert.NotEqual(BaselineSha, Sha([Eval(maturity: new EvExportMaturity(true, true, false, false))]));
        Assert.NotEqual(BaselineSha, Sha([Eval(maturity: new EvExportMaturity(true, true, true, true))]));
    }

    [Fact]
    public void ContentShaChangesWithCompatibility() =>
        Assert.NotEqual(BaselineSha, Sha([Eval(compatibility: AdapterCompatibility.Blocked)], AdapterSelectionOutcome.Blocked));

    [Fact]
    public void ContentShaChangesWithPrecedence() =>
        Assert.NotEqual(BaselineSha, Sha([Eval(precedence: 21)]));

    [Fact]
    public void ContentShaChangesWithProfileId() =>
        Assert.NotEqual(BaselineSha, Sha([Eval(profileId: "OTHER")]));

    [Fact]
    public void ContentShaChangesWithRequirement() =>
        Assert.NotEqual(BaselineSha, Sha([Eval(requirements: [Req(description: "outro")])]));

    [Fact]
    public void ContentShaChangesWithAdapterFinding() =>
        Assert.NotEqual(BaselineSha, Sha([Eval(findings: [Finding(reason: "achado")])]));

    [Fact]
    public void ContentShaChangesWithFindingErrorCategory() =>
        Assert.NotEqual(
            Sha([Eval(findings: [Finding(category: EvErrorCategory.None)])]),
            Sha([Eval(findings: [Finding(category: EvErrorCategory.ExecutionFailed)])]));

    [Fact]
    public void ContentShaChangesWithSelectionOutcome() =>
        Assert.NotEqual(BaselineSha, Sha([Eval()], AdapterSelectionOutcome.Ambiguous));

    [Fact]
    public void ContentShaChangesWithAdapterCandidates() =>
        Assert.NotEqual(BaselineSha, Sha([Eval()], candidates: [new EvAdapterId("adapter-a"), new EvAdapterId("adapter-z")]));

    [Fact]
    public void CanonicalBytesAreIndependentOfEvaluationAndCandidateOrder()
    {
        var evalA = Eval("adapter-a", precedence: 10, compatibility: AdapterCompatibility.Blocked);
        var evalB = Eval("adapter-b", precedence: 20);
        var forward = Serialize([evalA, evalB], candidates: [new EvAdapterId("adapter-a"), new EvAdapterId("adapter-b")]);
        var reversed = Serialize([evalB, evalA], candidates: [new EvAdapterId("adapter-b"), new EvAdapterId("adapter-a")]);
        Assert.Equal(forward.ContentSha256.Value, reversed.ContentSha256.Value);
        Assert.True(forward.Bytes.Span.SequenceEqual(reversed.Bytes.Span)); // bytes byte-a-byte idênticos
    }

    private static EvAdapterEvaluation EvalWithMaturity(EvExportMaturity? maturity) =>
        new(new EvAdapterId("adapter-a"), 1, AdapterCompatibility.Supported, [Cap()], [Req()], [], 20, "PROFILE", maturity);

    [Fact]
    public void ContentShaDistinguishesAbsentMaturityFromAllFalse()
    {
        var absent = Serialize([EvalWithMaturity(null)]);
        var allFalse = Serialize([EvalWithMaturity(new EvExportMaturity(false, false, false, false))]);
        Assert.NotEqual(absent.ContentSha256.Value, allFalse.ContentSha256.Value);

        // O evidence.json registra CLARAMENTE null versus objeto presente.
        using var absentDoc = JsonDocument.Parse(absent.Bytes);
        using var allFalseDoc = JsonDocument.Parse(allFalse.Bytes);
        var absentMaturity = absentDoc.RootElement.GetProperty("AdapterEvaluations")[0].GetProperty("Maturity");
        var presentMaturity = allFalseDoc.RootElement.GetProperty("AdapterEvaluations")[0].GetProperty("Maturity");
        Assert.Equal(JsonValueKind.Null, absentMaturity.ValueKind);
        Assert.Equal(JsonValueKind.Object, presentMaturity.ValueKind);
        Assert.False(presentMaturity.GetProperty("AutomatedFixtureValidated").GetBoolean());
    }

    [Fact]
    public void CanonicalBytesIndependentOfDuplicateAdapterIdOrder()
    {
        var first = Eval("adapter-a", precedence: 10);
        var second = Eval("adapter-a", precedence: 20);
        var forward = Serialize([first, second]);
        var reversed = Serialize([second, first]);
        Assert.True(forward.Bytes.Span.SequenceEqual(reversed.Bytes.Span));
        Assert.Equal(forward.ContentSha256.Value, reversed.ContentSha256.Value);
    }

    [Fact]
    public void CanonicalBytesIndependentOfDuplicateCapabilityOrder()
    {
        var forward = Serialize([Eval(capabilities:
            [Cap(EvCapabilityCodes.EvExportPstSupported, CapabilityAvailability.Available), Cap(EvCapabilityCodes.EvExportPstSupported, CapabilityAvailability.Unavailable)])]);
        var reversed = Serialize([Eval(capabilities:
            [Cap(EvCapabilityCodes.EvExportPstSupported, CapabilityAvailability.Unavailable), Cap(EvCapabilityCodes.EvExportPstSupported, CapabilityAvailability.Available)])]);
        Assert.True(forward.Bytes.Span.SequenceEqual(reversed.Bytes.Span));
    }

    [Fact]
    public void EvidenceJsonRecordsCompleteEvaluationsMaturityAndAuthoritativeFingerprints()
    {
        var maturity = new EvExportMaturity(RuntimeObserved: true, OfficialDocumentation: true, AutomatedFixtureValidated: true, LaboratoryValidated: false);
        var bytes = Serialize([Eval(
            maturity: maturity,
            requirements: [Req(EvCapabilityCodes.EvExportCmdletAvailable, "cmdlet presente")],
            findings: [Finding(EvDiscoveryResultCodes.PermissionInsufficient, EvCapabilityCodes.EvRequiredPermissions, "sem permissão", EvErrorCategory.PermissionDenied)])]);

        using var doc = JsonDocument.Parse(bytes.Bytes);
        var root = doc.RootElement;

        // Impressões digitais autoritativas registradas explicitamente.
        var fingerprints = root.GetProperty("ReservationFingerprints");
        Assert.Equal(ConfigHash.Value, fingerprints.GetProperty("ConfigurationHash").GetString());
        Assert.False(string.IsNullOrEmpty(fingerprints.GetProperty("SemanticEvidenceHash").GetString()));
        Assert.Equal("Supported", root.GetProperty("SelectionOutcome").GetString());

        // Hashes INTERNOS do capability set continuam claramente nomeados (não confundidos com os autoritativos).
        Assert.True(root.TryGetProperty("CapabilitySetConfigurationHash", out _));
        Assert.True(root.TryGetProperty("CapabilitySetEvidenceHash", out _));

        var evaluation = root.GetProperty("AdapterEvaluations").EnumerateArray().Single();
        Assert.Equal("adapter-a", evaluation.GetProperty("AdapterId").GetString());
        Assert.Equal("Supported", evaluation.GetProperty("Compatibility").GetString());
        Assert.Equal(20, evaluation.GetProperty("Precedence").GetInt32());
        Assert.Equal("PROFILE", evaluation.GetProperty("ProfileId").GetString());

        var mat = evaluation.GetProperty("Maturity");
        Assert.True(mat.GetProperty("RuntimeObserved").GetBoolean());
        Assert.True(mat.GetProperty("OfficialDocumentation").GetBoolean());
        Assert.True(mat.GetProperty("AutomatedFixtureValidated").GetBoolean());
        Assert.False(mat.GetProperty("LaboratoryValidated").GetBoolean()); // nunca homologado sem laboratório

        var requirement = evaluation.GetProperty("Requirements").EnumerateArray().Single();
        Assert.Equal(EvCapabilityCodes.EvExportCmdletAvailable, requirement.GetProperty("CapabilityCode").GetString());

        var finding = evaluation.GetProperty("Findings").EnumerateArray().Single();
        Assert.Equal("PermissionDenied", finding.GetProperty("ErrorCategory").GetString());
        Assert.Equal(EvCapabilityCodes.EvRequiredPermissions, finding.GetProperty("CapabilityCode").GetString());

        Assert.NotEmpty(evaluation.GetProperty("DeclaredCapabilities").EnumerateArray());
    }
}
