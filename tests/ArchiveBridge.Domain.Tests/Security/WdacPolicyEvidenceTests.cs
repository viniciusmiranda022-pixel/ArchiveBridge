using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Security;

/// <summary>
/// AB-I7-008 item 2 — <see cref="WdacAllowlistEntry"/>/<see cref="WdacPolicyEvidence"/>: nenhuma entrada
/// allow-all é aceita, <see cref="WdacPolicyEvidence.Validate"/> aceita apenas candidatos allowlisted, e
/// tampering das entradas/policy é detectado fail-closed por <see cref="WdacPolicyEvidence.Rehydrate"/>.
/// </summary>
public sealed class WdacPolicyEvidenceTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash WorkerHash = new(new string('c', 64));

    [Fact]
    public void AnEntryWithNoHashAndNoScopedPathIsRejectedAsAllowAll()
    {
        Assert.Throws<WdacPolicyInvariantViolationException>(() =>
            WdacAllowlistEntry.Create(publisher: null, sha256: null, pathRule: null));
    }

    [Fact]
    public void AnEntryWithAWildcardOnlyPathRuleAndNoHashIsRejectedAsAllowAll()
    {
        Assert.Throws<WdacPolicyInvariantViolationException>(() =>
            WdacAllowlistEntry.Create(publisher: "CN=Contoso", sha256: null, pathRule: "*"));
    }

    [Fact]
    public void AnEntryIdentifiedOnlyByHashIsAccepted()
    {
        var entry = WdacAllowlistEntry.Create(publisher: null, WorkerHash, pathRule: null);
        Assert.Equal(WorkerHash, entry.Sha256);
    }

    [Fact]
    public void AnEntryIdentifiedByPublisherAndAScopedPathRuleIsAccepted()
    {
        var entry = WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker\");
        Assert.Equal("CN=Contoso", entry.Publisher);
    }

    [Fact]
    public void ValidateAllowsAKnownHashAndDeniesAnUnknownHash()
    {
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, WorkerHash, pathRule: null) };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var allowedOutcome = policy.Validate(new WdacCandidateBinary(Publisher: null, WorkerHash, Path: null));
        var deniedOutcome = policy.Validate(new WdacCandidateBinary(Publisher: null, new Sha256Hash(new string('d', 64)), Path: null));

        Assert.Equal(WdacValidationOutcome.Allowed, allowedOutcome);
        Assert.Equal(WdacValidationOutcome.Denied, deniedOutcome);
    }

    [Fact]
    public void ValidateDeniesAPublisherMatchOutsideTheScopedPathRule()
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker\") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var outsideScope = policy.Validate(new WdacCandidateBinary("CN=Contoso", Sha256: null, @"C:\Windows\System32\evil.exe"));

        Assert.Equal(WdacValidationOutcome.Denied, outsideScope);
    }

    /// <summary>
    /// AB-I7-010 item 2 — <see cref="WdacAllowlistEntry.Matches"/> não pode degenerar em um mero prefixo
    /// lexical: a path rule 'C:\...\Worker' NUNCA pode corresponder a um caminho irmão como
    /// 'C:\...\WorkerEvil\...' apenas porque a string 'Worker' é um prefixo textual de 'WorkerEvil'.
    /// </summary>
    [Fact]
    public void ValidateDeniesASiblingDirectoryThatOnlySharesATextualPrefixWithThePathRule()
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var siblingOutcome = policy.Validate(
            new WdacCandidateBinary("CN=Contoso", Sha256: null, @"C:\Program Files\ArchiveBridge\WorkerEvil\payload.exe"));

        Assert.Equal(WdacValidationOutcome.Denied, siblingOutcome);
    }

    [Fact]
    public void ValidateAllowsALegitimateDescendantOfTheScopedPathRule()
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var descendantOutcome = policy.Validate(
            new WdacCandidateBinary("CN=Contoso", Sha256: null, @"C:\Program Files\ArchiveBridge\Worker\archivebridge-worker.exe"));

        Assert.Equal(WdacValidationOutcome.Allowed, descendantOutcome);
    }

    [Fact]
    public void ValidateAllowsAnExactPathMatchOfTheScopedPathRule()
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker\worker.exe") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var exactOutcome = policy.Validate(
            new WdacCandidateBinary("CN=Contoso", Sha256: null, @"C:\Program Files\ArchiveBridge\Worker\worker.exe"));

        Assert.Equal(WdacValidationOutcome.Allowed, exactOutcome);
    }

    [Fact]
    public void ValidateAllowsALegitimateNestedDescendantOfTheScopedPathRule()
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var nestedOutcome = policy.Validate(
            new WdacCandidateBinary("CN=Contoso", Sha256: null, @"C:\Program Files\ArchiveBridge\Worker\sub\payload.exe"));

        Assert.Equal(WdacValidationOutcome.Allowed, nestedOutcome);
    }

    /// <summary>
    /// AB-I7-011 — o candidato apresentado a <see cref="WdacPolicyEvidence.Validate"/> é entrada NÃO
    /// CONFIÁVEL: uma path rule aparentemente escopada (ex.: 'C:\Worker') NUNCA pode ser escapada por um
    /// candidato com segmentos relativos ('.'/'..'), separadores alternativos, UNC, ADS/curinga ou forma
    /// relativa — <see cref="WdacAllowlistEntry.Matches"/> precisa canonicalizar o <c>candidate.Path</c>
    /// com a MESMA rotina Windows-aware usada para a path rule antes de comparar, e recusar/Denied (nunca
    /// normalizar de forma permissiva) qualquer forma que não canonicalize sem ambiguidade.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\ArchiveBridge\Worker\..\WorkerEvil\payload.exe", "dot-dot segment escapes the scoped root")]
    [InlineData(@"C:\Program Files\ArchiveBridge\Worker\.\payload.exe", "dot segment")]
    [InlineData(@"ArchiveBridge\Worker\payload.exe", "relative path (no drive root)")]
    [InlineData(@"\\fileserver01\share\Worker\payload.exe", "UNC path")]
    [InlineData(@"C:\Program Files/ArchiveBridge/Worker/payload.exe", "forward slash separators")]
    [InlineData(@"C:\Program Files\ArchiveBridge\Worker\mixed/payload.exe", "mixed separators")]
    [InlineData(@"C:\Program Files\ArchiveBridge\Worker\payload.exe:hidden", "ADS/colon marker")]
    public void ValidateDeniesACandidatePathThatCannotBeCanonicalizedUnambiguously(string candidatePath, string reason)
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var outcome = policy.Validate(new WdacCandidateBinary("CN=Contoso", Sha256: null, candidatePath));

        Assert.True(WdacValidationOutcome.Denied == outcome, $"Expected Denied for {reason}: '{candidatePath}'.");
    }

    /// <summary>AB-I7-011 — matching por hash exato permanece inalterado, independentemente do <c>Path</c> do candidato (mesmo malformado/ambíguo).</summary>
    [Fact]
    public void ValidateByHashRemainsUnaffectedByAnUnrelatedOrMalformedCandidatePath()
    {
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, WorkerHash, pathRule: null) };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var allowedWithNullPath = policy.Validate(new WdacCandidateBinary(Publisher: null, WorkerHash, Path: null));
        var allowedWithMalformedPath = policy.Validate(
            new WdacCandidateBinary(Publisher: null, WorkerHash, @"C:\Worker\..\WorkerEvil\payload.exe"));

        Assert.Equal(WdacValidationOutcome.Allowed, allowedWithNullPath);
        Assert.Equal(WdacValidationOutcome.Allowed, allowedWithMalformedPath);
    }

    [Fact]
    public void ValidateMatchesThePathRuleCaseInsensitively()
    {
        var entries = new[] { WdacAllowlistEntry.Create("CN=Contoso", sha256: null, @"C:\Program Files\ArchiveBridge\Worker") };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var caseInsensitiveOutcome = policy.Validate(
            new WdacCandidateBinary("CN=Contoso", Sha256: null, @"c:\program files\archivebridge\worker\WORKER.EXE"));

        Assert.Equal(WdacValidationOutcome.Allowed, caseInsensitiveOutcome);
    }

    [Theory]
    [InlineData(@"ArchiveBridge\Worker")]
    [InlineData(@"C:\Program Files\..\Worker")]
    [InlineData(@"C:\Program Files\.\Worker")]
    [InlineData("C:\\")]
    [InlineData(@"\\fileserver01\share\Worker")]
    [InlineData(@"C:\Program Files/ArchiveBridge/Worker")]
    public void AnAmbiguousOrRelativePathRuleIsRejectedFailClosed(string pathRule)
    {
        Assert.Throws<WdacPolicyInvariantViolationException>(() =>
            WdacAllowlistEntry.Create("CN=Contoso", sha256: null, pathRule));
    }

    [Fact]
    public void RehydrateOfEntriesTamperedOutFromUnderThePolicyDigestIsRejectedFailClosed()
    {
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, WorkerHash, pathRule: null) };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        var tamperedEntries = new[] { WdacAllowlistEntry.Create(publisher: null, new Sha256Hash(new string('e', 64)), pathRule: null) };

        Assert.Throws<WdacPolicyIntegrityViolationException>(() =>
            WdacPolicyEvidence.Rehydrate(
                Tenant, Project, policy.PolicyVersion, tamperedEntries, policy.PolicyDigest, policy.IssuedBy,
                policy.IssuedByRole, policy.Correlation, policy.IssuedAtUtc, policy.SchemaVersion,
                policy.ContentFingerprint, policy.RecordHash));
    }

    [Fact]
    public void RehydrateOfATamperedRecordHashIsRejectedFailClosed()
    {
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, WorkerHash, pathRule: null) };
        var policy = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);

        Assert.Throws<WdacPolicyIntegrityViolationException>(() =>
            WdacPolicyEvidence.Rehydrate(
                Tenant, Project, policy.PolicyVersion, entries, policy.PolicyDigest, policy.IssuedBy,
                policy.IssuedByRole, policy.Correlation, policy.IssuedAtUtc, policy.SchemaVersion,
                policy.ContentFingerprint, new Sha256Hash(new string('f', 64))));
    }

    [Fact]
    public void TwoPoliciesWithTheSameEntriesProduceTheSameContentFingerprint()
    {
        var entries = new[] { WdacAllowlistEntry.Create(publisher: null, WorkerHash, pathRule: null) };
        var first = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 1, entries, "svc-security", "ServiceAccount", Correlation, Now);
        var second = WdacPolicyEvidence.Record(Tenant, Project, policyVersion: 2, entries, "another-svc", "ServiceAccount", CorrelationId.New(), Now.AddMinutes(5));

        Assert.Equal(first.ContentFingerprint, second.ContentFingerprint);
        Assert.NotEqual(first.RecordHash, second.RecordHash);
    }
}
