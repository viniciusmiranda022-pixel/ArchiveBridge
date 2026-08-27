using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Security;

/// <summary>
/// AB-I7-008 item 1 — <see cref="WorkerHardeningControlRecord"/>: ausência de medição nunca vira
/// <see cref="WorkerHardeningStatus.Pass"/>, um controle <see cref="WorkerHardeningApplicability.Unsupported"/>
/// nunca pode ser Pass (bloqueio estrutural), tamper-evidence via <see cref="WorkerHardeningControlRecord.Rehydrate"/>
/// e convergência idempotente por <see cref="WorkerHardeningControlRecord.ContentFingerprint"/>.
/// </summary>
public sealed class WorkerHardeningControlRecordTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Sha256Hash EvidenceFingerprint = new(new string('a', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotMeasuredCarriesNoMeasurementAndIsNeverPass()
    {
        var record = WorkerHardeningControlRecord.NotMeasured(
            Tenant, Project, WorkerHardeningControl.BitLocker, controlVersion: 1, notes: "ainda não verificado.",
            executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(WorkerHardeningStatus.NotMeasured, record.Status);
        Assert.Null(record.Measurement);
        Assert.Equal(WorkerHardeningControlRecord.NoEvidenceFingerprint, record.EvidenceFingerprint);
        Assert.Equal(WorkerHardeningApplicability.Required, record.Applicability);
    }

    [Fact]
    public void PassRequiresARealMeasurement()
    {
        var measurement = new WorkerHardeningMeasurement(Now, "local policy query");

        var record = WorkerHardeningControlRecord.Pass(
            Tenant, Project, WorkerHardeningControl.BitLocker, controlVersion: 1, measurement, EvidenceFingerprint,
            notes: "BitLocker habilitado.", executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(WorkerHardeningStatus.Pass, record.Status);
        Assert.Equal(measurement, record.Measurement);
    }

    [Fact]
    public void AnUnsupportedControlCanNeverResultInPassEvenWithARealMeasurement()
    {
        var measurement = new WorkerHardeningMeasurement(Now, "tenant policy query");

        Assert.Throws<WorkerHardeningInvariantViolationException>(() =>
            WorkerHardeningControlRecord.Pass(
                Tenant, Project, WorkerHardeningControl.MdeTenantPolicyEnforcement, controlVersion: 1, measurement,
                EvidenceFingerprint, notes: "alegação de política de tenant.", executedBy: "attacker",
                executedByRole: "Administrator", Correlation, Now));
    }

    [Fact]
    public void BlockedWithoutMeasurementRequiresADocumentedReason()
    {
        Assert.Throws<ArgumentException>(() =>
            WorkerHardeningControlRecord.Blocked(
                Tenant, Project, WorkerHardeningControl.RdpDenyByDefault, controlVersion: 1, measurement: null,
                EvidenceFingerprint, blockedReason: string.Empty, notes: string.Empty, executedBy: "svc-security",
                executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void NotesOrBlockedReasonWithAnAppearanceOfASecretIsRejectedFailClosed()
    {
        Assert.Throws<WorkerHardeningInvariantViolationException>(() =>
            WorkerHardeningControlRecord.Blocked(
                Tenant, Project, WorkerHardeningControl.OutboundRestricted, controlVersion: 1, measurement: null,
                EvidenceFingerprint, blockedReason: "Authorization: Bearer canary-secret-token", notes: string.Empty,
                executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void RehydrateOfATamperedContentFingerprintIsRejectedFailClosed()
    {
        var measurement = new WorkerHardeningMeasurement(Now, "local policy query");
        var record = WorkerHardeningControlRecord.Pass(
            Tenant, Project, WorkerHardeningControl.BitLocker, controlVersion: 1, measurement, EvidenceFingerprint,
            notes: "ok.", executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Throws<WorkerHardeningIntegrityViolationException>(() =>
            WorkerHardeningControlRecord.Rehydrate(
                Tenant, Project, WorkerHardeningControl.BitLocker, record.ControlVersion, record.Status,
                record.Measurement, record.EvidenceFingerprint, record.BlockedReason, "ADULTERADO",
                record.ExecutedBy, record.ExecutedByRole, record.Correlation, record.ExecutedAtUtc,
                record.SchemaVersion, record.ContentFingerprint, record.RecordHash));
    }

    [Fact]
    public void RehydrateOfATamperedRecordHashIsRejectedFailClosed()
    {
        var measurement = new WorkerHardeningMeasurement(Now, "local policy query");
        var record = WorkerHardeningControlRecord.Pass(
            Tenant, Project, WorkerHardeningControl.BitLocker, controlVersion: 1, measurement, EvidenceFingerprint,
            notes: "ok.", executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Throws<WorkerHardeningIntegrityViolationException>(() =>
            WorkerHardeningControlRecord.Rehydrate(
                Tenant, Project, WorkerHardeningControl.BitLocker, record.ControlVersion, record.Status,
                record.Measurement, record.EvidenceFingerprint, record.BlockedReason, record.Notes,
                record.ExecutedBy, record.ExecutedByRole, record.Correlation, record.ExecutedAtUtc,
                record.SchemaVersion, record.ContentFingerprint, new Sha256Hash(new string('f', 64))));
    }

    [Fact]
    public void TwoRecordsWithTheSameResultProduceTheSameContentFingerprint()
    {
        var measurement = new WorkerHardeningMeasurement(Now, "local policy query");
        var first = WorkerHardeningControlRecord.Pass(
            Tenant, Project, WorkerHardeningControl.BitLocker, controlVersion: 1, measurement, EvidenceFingerprint,
            notes: "ok.", executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);
        var second = WorkerHardeningControlRecord.Pass(
            Tenant, Project, WorkerHardeningControl.BitLocker, controlVersion: 2, measurement, EvidenceFingerprint,
            notes: "ok.", executedBy: "another-svc", executedByRole: "ServiceAccount", CorrelationId.New(), Now.AddMinutes(5));

        Assert.Equal(first.ContentFingerprint, second.ContentFingerprint);
        Assert.NotEqual(first.RecordHash, second.RecordHash);
    }

    [Fact]
    public void ApplicabilityIsAlwaysDerivedFromTheCatalogNeverFromTheCaller()
    {
        var record = WorkerHardeningControlRecord.NotMeasured(
            Tenant, Project, WorkerHardeningControl.MdeTenantPolicyEnforcement, controlVersion: 1,
            notes: string.Empty, executedBy: "svc-security", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(WorkerHardeningApplicability.Unsupported, record.Applicability);
    }
}
