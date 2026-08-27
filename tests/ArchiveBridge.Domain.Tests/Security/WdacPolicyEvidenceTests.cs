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
