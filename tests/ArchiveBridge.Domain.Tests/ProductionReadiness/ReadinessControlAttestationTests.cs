using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests.ProductionReadiness;

/// <summary>
/// AB-I8-001 — <see cref="ReadinessControlAttestation"/>: o bloqueio estrutural que impede atestação manual
/// de controles <see cref="ReadinessControlEvidenceSource.SystemDerived"/> (pen-test/RTO/RPO/SBOM/WDAC/
/// incident-response/hashes-manifests-lineage/backup-restore/policy-invariants NUNCA aprovados por alegação
/// humana), e tamper-evidence.
/// </summary>
public sealed class ReadinessControlAttestationTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private static readonly ReadinessEvidenceReference SomeEvidence = ReadinessEvidenceReference.Attested(SomeFingerprint, "adr-0031-approved-in-meeting-2026-08-15");

    [Theory]
    [InlineData("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH")]
    [InlineData("OPS.RTO_EXERCISED")]
    [InlineData("OPS.RPO_EXERCISED")]
    [InlineData("SEC.SBOM_AND_SIGNATURES")]
    [InlineData("SEC.WDAC_DEFENDER_PATCHING")]
    [InlineData("SEC.INCIDENT_RESPONSE_EXERCISED")]
    [InlineData("DATA.HASHES_MANIFESTS_LINEAGE_WORM")]
    [InlineData("DATA.BACKUP_RESTORE_TESTED")]
    [InlineData("M365.TARGET_ROOT_POLICY")]
    [InlineData("M365.IMPORT_LIMITS_100GB_500ROWS")]
    public void CreatingAnAttestationForASystemDerivedControlIsStructurallyRejected(string controlId)
    {
        Assert.Throws<ProductionReadinessAttestationNotAllowedException>(() =>
            ReadinessControlAttestation.Create(
                Tenant, Project, new ReadinessControlId(controlId), attestationVersion: 1, ReadinessControlStatus.Pass,
                SomeEvidence, reasonCode: string.Empty, "human-approver", "Approver", Correlation, Now));
    }

    [Fact]
    public void CreatingAnAttestationForArchiveLicenseQuotaIsStructurallyRejectedEvenThoughNoCanonicalSourceExists()
    {
        // AB-I8-003 blocker 1: diferente dos controles SystemDerived (evidência automatizada JÁ existe),
        // M365.ARCHIVE_LICENSE_QUOTA não tem NENHUMA fonte canônica hoje — mesmo assim, a ausência de
        // evidência nunca vira um checklist documental aprovável por atestação humana.
        Assert.Throws<ProductionReadinessAttestationNotAllowedException>(() =>
            ReadinessControlAttestation.Create(
                Tenant, Project, new ReadinessControlId("M365.ARCHIVE_LICENSE_QUOTA"), attestationVersion: 1, ReadinessControlStatus.Pass,
                SomeEvidence, reasonCode: string.Empty, "human-approver", "Approver", Correlation, Now));
    }

    [Fact]
    public void CreatingAnAttestationForAnUnknownControlIsRejected()
    {
        Assert.Throws<ProductionReadinessAttestationNotAllowedException>(() =>
            ReadinessControlAttestation.Create(
                Tenant, Project, new ReadinessControlId("ARCH.DOES_NOT_EXIST"), attestationVersion: 1, ReadinessControlStatus.Pass,
                SomeEvidence, reasonCode: string.Empty, "human-approver", "Approver", Correlation, Now));
    }

    [Fact]
    public void CreatingAnAttestationForAnAttestedControlSucceeds()
    {
        var attestation = ReadinessControlAttestation.Create(
            Tenant, Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 1, ReadinessControlStatus.Pass,
            SomeEvidence, reasonCode: string.Empty, "human-approver", "Approver", Correlation, Now);

        Assert.Equal(ReadinessControlStatus.Pass, attestation.Status);
        Assert.Equal(ReadinessEvidenceKind.ManualAttestation, attestation.Evidence.Kind);
    }

    [Fact]
    public void AnAttestationWithoutEvidenceIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ReadinessControlAttestation.Create(
                Tenant, Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 1, ReadinessControlStatus.NotMeasured,
                ReadinessEvidenceReference.None, reasonCode: string.Empty, "human-approver", "Approver", Correlation, Now));
    }

    [Fact]
    public void RehydratingAnUntamperedAttestationSucceeds()
    {
        var original = ReadinessControlAttestation.Create(
            Tenant, Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 2, ReadinessControlStatus.Pass,
            SomeEvidence, reasonCode: string.Empty, "human-approver", "Approver", Correlation, Now);

        var rehydrated = ReadinessControlAttestation.Rehydrate(
            original.Tenant, original.Project, original.ControlId, original.AttestationVersion, original.Status,
            original.Evidence, original.ReasonCode, original.SubmittedBy, original.SubmittedByRole, original.Correlation,
            original.SubmittedAtUtc, original.SchemaVersion, original.ContentFingerprint, original.RecordHash);

        Assert.Equal(original.RecordHash, rehydrated.RecordHash);
    }

    [Fact]
    public void RehydratingWithATamperedStatusIsRejected()
    {
        var original = ReadinessControlAttestation.Create(
            Tenant, Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 1, ReadinessControlStatus.Blocked,
            SomeEvidence, reasonCode: "PENDING_REVIEW", "human-approver", "Approver", Correlation, Now);

        Assert.Throws<ProductionReadinessIntegrityViolationException>(() =>
            ReadinessControlAttestation.Rehydrate(
                original.Tenant, original.Project, original.ControlId, original.AttestationVersion, ReadinessControlStatus.Pass,
                original.Evidence, original.ReasonCode, original.SubmittedBy, original.SubmittedByRole, original.Correlation,
                original.SubmittedAtUtc, original.SchemaVersion, original.ContentFingerprint, original.RecordHash));
    }

    [Fact]
    public void IdenticalAttestationContentConvergesToTheSameContentFingerprint()
    {
        var first = ReadinessControlAttestation.Create(
            Tenant, Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 1, ReadinessControlStatus.Pass,
            SomeEvidence, reasonCode: string.Empty, "actor-a", "Approver", Correlation, Now);
        var second = ReadinessControlAttestation.Create(
            Tenant, Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 9, ReadinessControlStatus.Pass,
            SomeEvidence, reasonCode: string.Empty, "actor-b", "Administrator", new CorrelationId(Guid.NewGuid()), Now + TimeSpan.FromDays(1));

        Assert.Equal(first.ContentFingerprint, second.ContentFingerprint);
    }
}
