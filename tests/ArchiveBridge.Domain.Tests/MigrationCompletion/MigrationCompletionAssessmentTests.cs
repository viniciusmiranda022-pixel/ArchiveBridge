using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests.MigrationCompletion;

/// <summary>
/// AB-I8-010 — <see cref="MigrationCompletionAssessment"/>: convergência idempotente de
/// <see cref="MigrationCompletionAssessment.AssessmentFingerprint"/>, e revalidação fail-closed de
/// integridade em <see cref="MigrationCompletionAssessment.Rehydrate"/> — <see cref="MigrationCompletionOutcome"/>
/// NUNCA representa a migração <c>Completed</c> (o enum deliberadamente não possui esse valor).
/// </summary>
public sealed class MigrationCompletionAssessmentTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly WaveId AnchorWave = new(Guid.NewGuid());
    private static readonly PurviewImportJobName AnchorPlannedJobName = PurviewImportJobName.Compute(Tenant, Project, AnchorWave, 1);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly Sha256Hash OtherHash = new(new string('b', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MigrationCompletionOutcomeNeverRepresentsCompleted()
    {
        var values = Enum.GetNames<MigrationCompletionOutcome>();
        Assert.Equal(2, values.Length);
        Assert.DoesNotContain(values, name => string.Equals(name, "Completed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssessmentFingerprintIsIndependentOfVersionActorAndTimestamp()
    {
        var resolved = AllCriteriaPassing();

        var first = MigrationCompletionAssessment.Compose(
            Tenant, Project, 1, AnchorWave, AnchorPlannedJobName, resolved, "approver-1", "Approver", CorrelationId.New(), Now);
        var second = MigrationCompletionAssessment.Compose(
            Tenant, Project, 9, AnchorWave, AnchorPlannedJobName, resolved, "approver-2", "Administrator", CorrelationId.New(), Now.AddDays(1));

        Assert.Equal(first.AssessmentFingerprint.Value, second.AssessmentFingerprint.Value);
    }

    [Fact]
    public void AssessmentFingerprintChangesWhenAnchorWaveChanges()
    {
        var resolved = AllCriteriaPassing();
        var otherWave = new WaveId(Guid.NewGuid());

        var first = MigrationCompletionAssessment.Compose(
            Tenant, Project, 1, AnchorWave, AnchorPlannedJobName, resolved, "approver-1", "Approver", CorrelationId.New(), Now);
        var second = MigrationCompletionAssessment.Compose(
            Tenant, Project, 1, otherWave, AnchorPlannedJobName, resolved, "approver-1", "Approver", CorrelationId.New(), Now);

        Assert.NotEqual(first.AssessmentFingerprint.Value, second.AssessmentFingerprint.Value);
    }

    [Fact]
    public void ComposeWithAllCriteriaPassingIsEligible()
    {
        var assessment = MigrationCompletionAssessment.Compose(
            Tenant, Project, 1, AnchorWave, AnchorPlannedJobName, AllCriteriaPassing(), "approver-1", "Approver", CorrelationId.New(), Now);

        Assert.Equal(MigrationCompletionOutcome.Eligible, assessment.Outcome);
        Assert.Empty(assessment.Blockers);
    }

    [Fact]
    public void RehydrateThrowsWhenAssessmentFingerprintIsTampered()
    {
        var assessment = ComposeEligible();

        var ex = Assert.Throws<MigrationCompletionIntegrityViolationException>(() => MigrationCompletionAssessment.Rehydrate(
            assessment.Tenant, assessment.Project, assessment.AssessmentVersion, assessment.AnchorWave, assessment.AnchorPlannedJobName,
            assessment.CriterionResults, assessment.Outcome, OtherHash /* tampered */, assessment.SubmittedBy, assessment.SubmittedByRole,
            assessment.Correlation, assessment.GeneratedAtUtc, assessment.SchemaVersion, assessment.AssessmentHash));

        Assert.Contains("assessment_fingerprint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RehydrateThrowsWhenOutcomeIsTamperedWithoutTouchingTheCriterionResults()
    {
        var assessment = ComposeEligible();

        var ex = Assert.Throws<MigrationCompletionIntegrityViolationException>(() => MigrationCompletionAssessment.Rehydrate(
            assessment.Tenant, assessment.Project, assessment.AssessmentVersion, assessment.AnchorWave, assessment.AnchorPlannedJobName,
            assessment.CriterionResults, MigrationCompletionOutcome.Blocked /* tampered */, assessment.AssessmentFingerprint,
            assessment.SubmittedBy, assessment.SubmittedByRole, assessment.Correlation, assessment.GeneratedAtUtc, assessment.SchemaVersion,
            assessment.AssessmentHash));

        Assert.Contains("outcome", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RehydrateRoundTripsSuccessfullyForAnUntamperedRecord()
    {
        var assessment = ComposeEligible();

        var rehydrated = MigrationCompletionAssessment.Rehydrate(
            assessment.Tenant, assessment.Project, assessment.AssessmentVersion, assessment.AnchorWave, assessment.AnchorPlannedJobName,
            assessment.CriterionResults, assessment.Outcome, assessment.AssessmentFingerprint, assessment.SubmittedBy,
            assessment.SubmittedByRole, assessment.Correlation, assessment.GeneratedAtUtc, assessment.SchemaVersion, assessment.AssessmentHash);

        Assert.Equal(MigrationCompletionOutcome.Eligible, rehydrated.Outcome);
    }

    private static MigrationCompletionAssessment ComposeEligible() =>
        MigrationCompletionAssessment.Compose(
            Tenant, Project, 1, AnchorWave, AnchorPlannedJobName, AllCriteriaPassing(), "approver-1", "Approver", CorrelationId.New(), Now);

    private static Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> AllCriteriaPassing()
    {
        var resolved = new Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult>();
        foreach (var definition in MigrationCompletionCriterionCatalog.AllCriteria)
        {
            resolved[definition.Id] = MigrationCompletionCriterionResult.Create(
                definition.Id, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeHash, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
