using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Security;

/// <summary>
/// AB-I7-008 item 5 — <see cref="IncidentResponseDrillRecord"/>: disposition com aparência de segredo/PII
/// é recusada fail-closed, timestamps invertidos são recusados, e tampering é detectado por
/// <see cref="IncidentResponseDrillRecord.Rehydrate"/>.
/// </summary>
public sealed class IncidentResponseDrillRecordTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Sha256Hash EvidenceDigest = new(new string('a', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordAcceptsAContainedDrillWithASafeDisposition()
    {
        var record = IncidentResponseDrillRecord.Record(
            Tenant, Project, IncidentResponseDrillType.SecretLeakCanary, drillVersion: 1,
            IncidentResponseDrillOutcome.Contained, Now, Now.AddSeconds(1), EvidenceDigest,
            disposition: "Canary secret was redacted before persistence; no raw secret reached storage.",
            executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(IncidentResponseDrillOutcome.Contained, record.Outcome);
    }

    [Fact]
    public void CompletedBeforeStartedIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IncidentResponseDrillRecord.Record(
                Tenant, Project, IncidentResponseDrillType.SecretLeakCanary, drillVersion: 1,
                IncidentResponseDrillOutcome.Contained, Now, Now.AddSeconds(-1), EvidenceDigest,
                disposition: "ok.", executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Theory]
    [InlineData("Authorization: Bearer canary-token-abc")]
    [InlineData("Cookie: session=canary-cookie-value")]
    [InlineData("user.canary@contoso.com replied to the drill")]
    [InlineData(@"Evidence path: \\fileserver\share\canary.pst")]
    [InlineData("Evidence link: https://contoso.example.com/report?token=opaque-canary-value")]
    [InlineData("Evidence link: https://contoso.example.com/report?code=canary-code-value")]
    public void ADispositionWithAnAppearanceOfASecretOrPiiIsRejectedFailClosed(string unsafeDisposition)
    {
        Assert.Throws<IncidentResponseInvariantViolationException>(() =>
            IncidentResponseDrillRecord.Record(
                Tenant, Project, IncidentResponseDrillType.SecretLeakCanary, drillVersion: 1,
                IncidentResponseDrillOutcome.Contained, Now, Now.AddSeconds(1), EvidenceDigest, unsafeDisposition,
                executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void RehydrateOfATamperedDispositionIsRejectedFailClosed()
    {
        var record = IncidentResponseDrillRecord.Record(
            Tenant, Project, IncidentResponseDrillType.HashMismatchTampering, drillVersion: 1,
            IncidentResponseDrillOutcome.Contained, Now, Now.AddSeconds(1), EvidenceDigest,
            disposition: "Tampering detected as expected.", executedBy: "svc-security", executedByRole: "ServiceAccount",
            Correlation, Now);

        Assert.Throws<IncidentResponseIntegrityViolationException>(() =>
            IncidentResponseDrillRecord.Rehydrate(
                Tenant, Project, record.DrillType, record.DrillVersion, record.Outcome, record.StartedAtUtc,
                record.CompletedAtUtc, record.EvidenceDigest, "ADULTERADO", record.ExecutedBy, record.ExecutedByRole,
                record.Correlation, record.RecordedAtUtc, record.SchemaVersion, record.ContentFingerprint, record.RecordHash));
    }

    [Fact]
    public void TwoRecordsWithTheSameResultProduceTheSameContentFingerprint()
    {
        var first = IncidentResponseDrillRecord.Record(
            Tenant, Project, IncidentResponseDrillType.CrossTenantDenial, drillVersion: 1,
            IncidentResponseDrillOutcome.Contained, Now, Now.AddSeconds(1), EvidenceDigest, "Cross-tenant read denied by RLS.",
            executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);
        var second = IncidentResponseDrillRecord.Record(
            Tenant, Project, IncidentResponseDrillType.CrossTenantDenial, drillVersion: 2,
            IncidentResponseDrillOutcome.Contained, Now, Now.AddSeconds(1), EvidenceDigest, "Cross-tenant read denied by RLS.",
            executedBy: "another-svc", executedByRole: "ServiceAccount", CorrelationId.New(), Now.AddMinutes(5));

        Assert.Equal(first.ContentFingerprint, second.ContentFingerprint);
        Assert.NotEqual(first.RecordHash, second.RecordHash);
    }
}
