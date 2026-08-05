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

    // ---- Duplicidade recusada FAIL-CLOSED pelo serializer (alinhada às constraints SQL) ------------------

    [Fact]
    public void SerializerRejectsDuplicateAdapterIdFailClosed() =>
        Assert.Throws<EvDiscoveryInvariantException>(() => Serialize([Eval("adapter-a", precedence: 10), Eval("adapter-a", precedence: 20)]));

    [Fact]
    public void SerializerRejectsDuplicateCapabilityCodeWithinEvaluationFailClosed() =>
        Assert.Throws<EvDiscoveryInvariantException>(() => Serialize([Eval(capabilities:
            [Cap(EvCapabilityCodes.EvExportPstSupported), Cap(EvCapabilityCodes.EvExportPstSupported, CapabilityAvailability.Unavailable)])]));

    // ---- Ausência versus valor no artefato: cada distinção muda o ContentSha256 --------------------------

    private static EvCapabilitySet CapabilitySetOf(EvAdapterId? adapterId, int? adapterVersion) =>
        EvCapabilitySet.Create(Env, adapterId, adapterVersion, EvDiscoverySchema.Version, [Cap()], EvDiscoveryStatus.Ready);

    private static EvDiscoveryEvidenceBytes SerializeRaw(
        EvCapabilitySet capabilitySet, EvAdapterEvaluation[] evaluations, EvAdapterEvaluation? selected, EvExportSignature? signature = null)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", "15.1", "15.1", "PowerShell", Now);
        var selection = new EvAdapterSelection(
            AdapterSelectionOutcome.Supported, selected, [.. evaluations.Select(static e => e.AdapterId)], evaluations, []);
        var result = new EvDiscoveryRunResult(
            DiscoveryRunId.New(), identity, capabilitySet, selection, signature, [], EvDiscoveryStatus.Ready,
            new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryCompleted), Now, Now);
        return Serializer.Serialize(result, ConfigHash, EvDiscoverySemanticFingerprint.Compute(result));
    }

    [Fact]
    public void ContentShaDistinguishesProfileIdNullFromEmpty() =>
        Assert.NotEqual(Sha([Eval(profileId: null)]), Sha([Eval(profileId: "")]));

    [Fact]
    public void ContentShaDistinguishesBlockingReasonNullFromEmpty()
    {
        var nullReason = new EvCapability(new EvCapabilityCode(EvCapabilityCodes.EvExportPstSupported), 1, CapabilityAvailability.Unavailable, "ref", null, Now);
        var emptyReason = new EvCapability(new EvCapabilityCode(EvCapabilityCodes.EvExportPstSupported), 1, CapabilityAvailability.Unavailable, "ref", string.Empty, Now);
        Assert.NotEqual(Sha([Eval(capabilities: [nullReason])]), Sha([Eval(capabilities: [emptyReason])]));
    }

    [Fact]
    public void ContentShaDistinguishesFindingCapabilityCodeAbsentFromPresent() =>
        Assert.NotEqual(
            Sha([Eval(findings: [Finding(capabilityCode: null)])]),
            Sha([Eval(findings: [Finding(capabilityCode: EvCapabilityCodes.EvExportPstSupported)])]));

    [Fact]
    public void ContentShaDistinguishesCapabilitySetAdapterVersionNullFromZero()
    {
        var nullVersion = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), null), [Eval()], Eval());
        var zeroVersion = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 0), [Eval()], Eval());
        Assert.NotEqual(nullVersion.ContentSha256.Value, zeroVersion.ContentSha256.Value);
    }

    [Fact]
    public void ContentShaDistinguishesSelectedNullFromNoneAdapter()
    {
        var noneEval = Eval("<none>");
        var notSelected = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 1), [noneEval], selected: null);
        var selectedNone = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 1), [noneEval], selected: noneEval);
        Assert.NotEqual(notSelected.ContentSha256.Value, selectedNone.ContentSha256.Value);
    }

    [Fact]
    public void ContentShaDistinguishesSignatureAbsentFromPresent()
    {
        var signature = EvExportSignature.Create("Export-EVArchive", "Mod", "15.1", "Cmdlet", ["ArchiveId"], ["ArchiveId"], ["Default"], Now);
        var absent = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 1), [Eval()], Eval(), signature: null);
        var present = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 1), [Eval()], Eval(), signature: signature);
        Assert.NotEqual(absent.ContentSha256.Value, present.ContentSha256.Value);
    }

    [Fact]
    public void ContentShaEncodesUnitSeparatorInTextField() =>
        Assert.NotEqual(Sha([Eval(profileId: "a\u001fb")]), Sha([Eval(profileId: "ab")]));

    // ---- Signature.ObservedVersion: conteúdo FACTUAL, no hash semântico E no evidence.json (6ª revisão) ----

    // Mesma FORMA (SignatureHash idêntico — não depende da versão textual); só ObservedVersion/DiscoveredAtUtc variam.
    private static EvExportSignature SignatureWith(string observedVersion, DateTimeOffset discoveredAt) =>
        EvExportSignature.Create(
            "Export-EVArchive", "Symantec.EnterpriseVault.PowerShell", observedVersion, "Cmdlet",
            ["ArchiveId", "OutputDirectory"], ["ArchiveId"], ["Default"], discoveredAt);

    private static EvDiscoveryRunResult ResultWithSignature(EvExportSignature signature)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", "15.1", "15.1", "PowerShell", Now);
        var evaluation = Eval();
        var selection = new EvAdapterSelection(
            AdapterSelectionOutcome.Supported, evaluation, [evaluation.AdapterId], [evaluation], []);
        return new EvDiscoveryRunResult(
            DiscoveryRunId.New(), identity, CapabilitySetOf(new EvAdapterId("adapter-a"), 1), selection, signature, [],
            EvDiscoveryStatus.Ready, new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryCompleted), Now, Now);
    }

    [Fact]
    public void SignatureObservedVersionIsIsolatedInHashArtifactAndContentSha()
    {
        var sigA = SignatureWith("15.1", Now);
        var sigB = SignatureWith("15.2", Now);
        // A forma (SignatureHash) é IDÊNTICA — só ObservedVersion difere, isolando o campo sob teste.
        Assert.Equal(sigA.SignatureHash.Value, sigB.SignatureHash.Value);

        var resultA = ResultWithSignature(sigA);
        var resultB = ResultWithSignature(sigB);
        var hashA = EvDiscoverySemanticFingerprint.Compute(resultA);
        var hashB = EvDiscoverySemanticFingerprint.Compute(resultB);
        var artifactA = Serializer.Serialize(resultA, ConfigHash, hashA);
        var artifactB = Serializer.Serialize(resultB, ConfigHash, hashB);

        // 1. SemanticEvidenceHash difere. 2. evidence.json (bytes) difere. 3. ContentSha256 difere.
        Assert.NotEqual(hashA.Value, hashB.Value);
        Assert.False(artifactA.Bytes.Span.SequenceEqual(artifactB.Bytes.Span));
        Assert.NotEqual(artifactA.ContentSha256.Value, artifactB.ContentSha256.Value);

        // 4 e 5. O JSON registra o valor factual correto em cada caso.
        using var docA = JsonDocument.Parse(artifactA.Bytes);
        using var docB = JsonDocument.Parse(artifactB.Bytes);
        Assert.Equal("15.1", docA.RootElement.GetProperty("Signature").GetProperty("ObservedVersion").GetString());
        Assert.Equal("15.2", docB.RootElement.GetProperty("Signature").GetProperty("ObservedVersion").GetString());

        // 7. DiscoveredAtUtc permanece FORA do artefato e do hash semântico (campo volátil).
        Assert.False(docA.RootElement.GetProperty("Signature").TryGetProperty("DiscoveredAtUtc", out _));
        var laterResult = ResultWithSignature(SignatureWith("15.1", Now.AddHours(3)));
        Assert.Equal(hashA.Value, EvDiscoverySemanticFingerprint.Compute(laterResult).Value);
        Assert.Equal(
            artifactA.ContentSha256.Value,
            Serializer.Serialize(laterResult, ConfigHash, hashA).ContentSha256.Value);
    }

    [Fact]
    public void CanonicalBytesIgnoreValidCollectionOrderWithSignaturePresent()
    {
        // 6. Inverter coleções válidas e únicas continua NÃO alterando os bytes (com assinatura presente).
        var signature = SignatureWith("15.1", Now);
        var evalA = Eval("adapter-a", precedence: 10, compatibility: AdapterCompatibility.Blocked);
        var evalB = Eval("adapter-b", precedence: 20);
        var forward = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 1), [evalA, evalB], evalB, signature);
        var reversed = SerializeRaw(CapabilitySetOf(new EvAdapterId("adapter-a"), 1), [evalB, evalA], evalB, signature);
        Assert.True(forward.Bytes.Span.SequenceEqual(reversed.Bytes.Span));
        Assert.Equal(forward.ContentSha256.Value, reversed.ContentSha256.Value);
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
