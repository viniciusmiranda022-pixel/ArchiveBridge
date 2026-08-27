using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Recovery;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Recovery;

/// <summary>
/// AB-I7-005 — <see cref="RecoveryReadinessRecord"/>: os gates fail-closed de RTO/RPO
/// (Unknown/NotMeasured nunca vira Pass), o bloqueio estrutural de HA, tamper-evidence (Rehydrate) e
/// convergência idempotente por <see cref="RecoveryReadinessRecord.ExerciseFingerprint"/>.
/// </summary>
public sealed class RecoveryReadinessRecordTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Sha256Hash EvidenceFingerprint = new(new string('a', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PassRequiresARealMeasurement()
    {
        // Pass() só aceita RecoveryObjectiveMeasurement (não-anulável) — impossível compilar um Pass sem
        // medição; este teste prova o caminho equivalente através da store (BuildRecord) não é necessário
        // aqui porque o próprio compilador já impede a chamada. Em vez disso, prova que a AUSÊNCIA de
        // medição em NotMeasured nunca produz Status.Pass.
        var record = RecoveryReadinessRecord.NotMeasured(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            objectiveThreshold: TimeSpan.FromHours(4), notes: "drill ainda não executado.", executedBy: "svc-recovery",
            executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(RecoveryReadinessStatus.NotMeasured, record.Status);
        Assert.Null(record.Measurement);
        Assert.Equal(RecoveryReadinessRecord.NoEvidenceFingerprint, record.EvidenceFingerprint);
    }

    [Fact]
    public void PassWithinTheObjectiveThresholdSucceeds()
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromHours(1));

        var record = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            objectiveThreshold: TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "restore drill ok.",
            executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(RecoveryReadinessStatus.Pass, record.Status);
        Assert.Equal(measurement, record.Measurement);
    }

    [Fact]
    public void PassThatExceedsTheObjectiveThresholdIsRejected()
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromHours(5));

        Assert.Throws<RecoveryReadinessObjectiveNotMetException>(() =>
            RecoveryReadinessRecord.Pass(
                Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
                objectiveThreshold: TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "excedeu o RTO.",
                executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void HaFailoverCanNeverResultInPass()
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(1));

        Assert.Throws<RecoveryReadinessObjectiveNotMetException>(() =>
            RecoveryReadinessRecord.Pass(
                Tenant, Project, RecoveryExerciseType.HaFailover, exerciseVersion: 1, RecoveryObjective.None,
                objectiveThreshold: null, measurement, EvidenceFingerprint, notes: "tentativa indevida.",
                executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now));
    }

    // ---- AB-I7-007 item 2 (Blocker 2): RPO nunca é Pass nesta baseline — medir a duração entre dois
    // exercícios consecutivos NÃO é RPO (a janela de perda de dados entre o último estado confirmado antes
    // de uma falha real e o último estado recuperável depois dela). Sem um drill de failure-boundary
    // dedicado, o desfecho permanece explicitamente Blocked/NotMeasured — nenhum caminho de código pode
    // promover ControlPlaneRpo/EvidenceLogicalRpo a Pass com a métrica errada. ----

    [Theory]
    [InlineData(RecoveryObjective.ControlPlaneRpo)]
    [InlineData(RecoveryObjective.EvidenceLogicalRpo)]
    public void RpoObjectivesCanNeverResultInPassUntilAFailureBoundaryDrillExists(RecoveryObjective rpoObjective)
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(1));

        Assert.Throws<RecoveryReadinessObjectiveNotMetException>(() =>
            RecoveryReadinessRecord.Pass(
                Tenant, Project, RecoveryExerciseType.PendingWorkRebuild, exerciseVersion: 1, rpoObjective,
                objectiveThreshold: null, measurement, EvidenceFingerprint, notes: "tentativa indevida de medir RPO pela duração entre exercícios.",
                executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Theory]
    [InlineData(RecoveryObjective.ControlPlaneRpo)]
    [InlineData(RecoveryObjective.EvidenceLogicalRpo)]
    public void RpoObjectivesRemainExplicitlyBlockedWithADocumentedFailureDomainUntilAFailureBoundaryDrillExists(RecoveryObjective rpoObjective)
    {
        var record = RecoveryReadinessRecord.Blocked(
            Tenant, Project, RecoveryExerciseType.PendingWorkRebuild, exerciseVersion: 1, rpoObjective,
            objectiveThreshold: null, measurement: null, RecoveryReadinessRecord.NoEvidenceFingerprint,
            failureDomain: "Nenhum drill de failure-boundary real existe hoje para medir RPO objetivamente.",
            notes: string.Empty, executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(RecoveryReadinessStatus.Blocked, record.Status);
        Assert.NotEmpty(record.FailureDomain);
    }

    [Fact]
    public void HaFailoverIsExplicitlyBlockedWithADocumentedFailureDomain()
    {
        var record = RecoveryReadinessRecord.Blocked(
            Tenant, Project, RecoveryExerciseType.HaFailover, exerciseVersion: 1, RecoveryObjective.None,
            objectiveThreshold: null, measurement: null, RecoveryReadinessRecord.NoEvidenceFingerprint,
            failureDomain: "Proteção de segredo single-node (DPAPI) sem mecanismo de failover aprovado.",
            notes: string.Empty, executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.Equal(RecoveryReadinessStatus.Blocked, record.Status);
        Assert.NotEmpty(record.FailureDomain);
    }

    [Fact]
    public void BlockedByArchitectureWithoutAMeasurementRequiresADocumentedFailureDomain()
    {
        Assert.Throws<ArgumentException>(() =>
            RecoveryReadinessRecord.Blocked(
                Tenant, Project, RecoveryExerciseType.HaFailover, exerciseVersion: 1, RecoveryObjective.None,
                objectiveThreshold: null, measurement: null, RecoveryReadinessRecord.NoEvidenceFingerprint,
                failureDomain: string.Empty, notes: string.Empty, executedBy: "svc-recovery",
                executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void RehydrateOfAnUntamperedRecordSucceeds()
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(90));
        var record = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 3, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "ok", executedBy: "svc-recovery",
            executedByRole: "ServiceAccount", Correlation, Now);

        var rehydrated = RecoveryReadinessRecord.Rehydrate(
            record.Tenant, record.Project, record.ExerciseType, record.ExerciseVersion, record.Status, record.Objective,
            record.ObjectiveThreshold, record.Measurement, record.EvidenceFingerprint, record.FailureDomain, record.Notes,
            record.ExerciseFingerprint, record.ExecutedBy, record.ExecutedByRole, record.Correlation, record.ExecutedAtUtc,
            record.SchemaVersion, record.RecordHash);

        Assert.Equal(record.RecordHash, rehydrated.RecordHash);
        Assert.Equal(record.Status, rehydrated.Status);
    }

    [Fact]
    public void RehydrateOfARecordWithATamperedHashIsRejectedFailClosed()
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(90));
        var record = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "ok", executedBy: "svc-recovery",
            executedByRole: "ServiceAccount", Correlation, Now);

        var tamperedHash = new Sha256Hash(new string('0', 64));

        Assert.Throws<RecoveryReadinessIntegrityViolationException>(() =>
            RecoveryReadinessRecord.Rehydrate(
                record.Tenant, record.Project, record.ExerciseType, record.ExerciseVersion, record.Status, record.Objective,
                record.ObjectiveThreshold, record.Measurement, record.EvidenceFingerprint, record.FailureDomain, record.Notes,
                record.ExerciseFingerprint, record.ExecutedBy, record.ExecutedByRole, record.Correlation, record.ExecutedAtUtc,
                record.SchemaVersion, tamperedHash));
    }

    [Fact]
    public void RehydrateOfARecordWithATamperedEvidenceFingerprintIsRejectedFailClosedEvenIfRecordHashWereRecomputed()
    {
        // Adulterar evidence_fingerprint sem atualizar exercise_fingerprint (a coluna persistida
        // separadamente) é detectado ANTES de qualquer outra validação — mesmo princípio do
        // evaluation_fingerprint isolado de ReconciliationCertificate (AB-I6-015).
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(90));
        var record = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "ok", executedBy: "svc-recovery",
            executedByRole: "ServiceAccount", Correlation, Now);

        var tamperedEvidence = new Sha256Hash(new string('9', 64));

        Assert.Throws<RecoveryReadinessIntegrityViolationException>(() =>
            RecoveryReadinessRecord.Rehydrate(
                record.Tenant, record.Project, record.ExerciseType, record.ExerciseVersion, record.Status, record.Objective,
                record.ObjectiveThreshold, record.Measurement, tamperedEvidence, record.FailureDomain, record.Notes,
                record.ExerciseFingerprint, record.ExecutedBy, record.ExecutedByRole, record.Correlation, record.ExecutedAtUtc,
                record.SchemaVersion, record.RecordHash));
    }

    [Fact]
    public void TwoExecutionsWithIdenticalResultsConvergeToTheSameExerciseFingerprintRegardlessOfActorOrVersion()
    {
        var measurement = new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(90));

        var first = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "ok", executedBy: "worker-a",
            executedByRole: "ServiceAccount", Correlation, Now);

        var second = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 7, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), measurement, EvidenceFingerprint, notes: "ok", executedBy: "worker-b",
            executedByRole: "ServiceAccount", new CorrelationId(Guid.NewGuid()), Now.AddMinutes(5));

        Assert.Equal(first.ExerciseFingerprint, second.ExerciseFingerprint);
        Assert.NotEqual(first.RecordHash, second.RecordHash);
    }

    [Fact]
    public void ADifferentMeasurementProducesADifferentExerciseFingerprint()
    {
        var first = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(90)), EvidenceFingerprint,
            notes: "ok", executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);

        var second = RecoveryReadinessRecord.Pass(
            Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), new RecoveryObjectiveMeasurement(Now, Now + TimeSpan.FromMinutes(91)), EvidenceFingerprint,
            notes: "ok", executedBy: "svc-recovery", executedByRole: "ServiceAccount", Correlation, Now);

        Assert.NotEqual(first.ExerciseFingerprint, second.ExerciseFingerprint);
    }

    [Fact]
    public void ObjectiveThresholdMustBePositiveWhenAnObjectiveIsSpecified()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoveryReadinessRecord.NotMeasured(
                Tenant, Project, RecoveryExerciseType.RestoreDrill, exerciseVersion: 1, RecoveryObjective.ControlPlaneRto,
                objectiveThreshold: TimeSpan.Zero, notes: string.Empty, executedBy: "svc-recovery",
                executedByRole: "ServiceAccount", Correlation, Now));
    }

    [Fact]
    public void NoObjectiveThresholdIsAllowedWhenObjectiveIsNone()
    {
        Assert.Throws<ArgumentException>(() =>
            RecoveryReadinessRecord.NotMeasured(
                Tenant, Project, RecoveryExerciseType.ArtifactEvidenceRecovery, exerciseVersion: 1, RecoveryObjective.None,
                objectiveThreshold: TimeSpan.FromHours(1), notes: string.Empty, executedBy: "svc-recovery",
                executedByRole: "ServiceAccount", Correlation, Now));
    }
}
