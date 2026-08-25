using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I6-001 — <see cref="PurviewImportJobName"/> (nome planejado determinístico/server-side, alfabeto do
/// portal), <see cref="PurviewImportJobPlan"/> e <see cref="PurviewImportJobObservation"/> (evidência
/// append-only, hash tamper-evident na reidratação, limites plausíveis do horário observado).
/// </summary>
public sealed class PurviewImportJobDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly WaveId Wave = WaveId.New();

    private static PurviewMappingGenerationFingerprint Fingerprint(string seed) =>
        PurviewMappingGenerationFingerprint.Compute(
            Wave, TargetRootFolder.ForWave("prj01", "w001"), DeterministicHash.Compute([seed]), DeterministicHash.Compute(["attempt"]), 1, 1);

    [Fact]
    public void PlannedJobNameIsDeterministicForTheSameScopeAndAttempt()
    {
        var first = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var second = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PlannedJobNameDiffersWhenAttemptSequenceDiffers()
    {
        var first = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var second = PurviewImportJobName.Compute(Tenant, Project, Wave, 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PlannedJobNameOnlyContainsLowercaseDigitsHyphenAndUnderscore()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);

        Assert.Matches("^[a-z0-9_-]+$", name.Value);
        Assert.True(name.Value.Length <= PurviewImportJobName.MaxLength);
    }

    [Fact]
    public void PlannedJobNameRejectsAttemptSequenceBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PurviewImportJobName.Compute(Tenant, Project, Wave, 0));
    }

    [Theory]
    [InlineData("Ab-Imp-Uppercase-1")]
    [InlineData("ab imp with space-1")]
    [InlineData("")]
    public void PlannedJobNameFromPersistedValueRejectsAnyCharacterOutsideThePortalAlphabet(string tampered)
    {
        Assert.Throws<PurviewImportJobIntegrityViolationException>(() => PurviewImportJobName.FromPersistedValue(tampered));
    }

    [Fact]
    public void PlanRehydrateAcceptsAnUnmodifiedRoundTrip()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var plan = PurviewImportJobPlan.Create(Tenant, Project, Wave, 1, name, Fingerprint("evidence"), "operator", Now);

        var rehydrated = PurviewImportJobPlan.Rehydrate(
            plan.Tenant, plan.Project, plan.Wave, plan.AttemptSequence, plan.PlannedJobName, plan.EvidenceFingerprint,
            plan.CreatedBy, plan.CreatedAtUtc, plan.PlanHash);

        Assert.Equal(plan.PlanHash, rehydrated.PlanHash);
    }

    [Fact]
    public void PlanRehydrateFailsClosedWhenTheFingerprintWasTamperedAfterPersistence()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var plan = PurviewImportJobPlan.Create(Tenant, Project, Wave, 1, name, Fingerprint("evidence"), "operator", Now);
        var tamperedFingerprint = Fingerprint("tampered");

        Assert.Throws<PurviewImportJobIntegrityViolationException>(() =>
            PurviewImportJobPlan.Rehydrate(
                plan.Tenant, plan.Project, plan.Wave, plan.AttemptSequence, plan.PlannedJobName, tamperedFingerprint,
                plan.CreatedBy, plan.CreatedAtUtc, plan.PlanHash));
    }

    [Fact]
    public void PlanRehydrateFailsClosedWhenTheAttemptSequenceWasTamperedAfterPersistence()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var plan = PurviewImportJobPlan.Create(Tenant, Project, Wave, 1, name, Fingerprint("evidence"), "operator", Now);

        Assert.Throws<PurviewImportJobIntegrityViolationException>(() =>
            PurviewImportJobPlan.Rehydrate(
                plan.Tenant, plan.Project, plan.Wave, attemptSequence: 2, plan.PlannedJobName, plan.EvidenceFingerprint,
                plan.CreatedBy, plan.CreatedAtUtc, plan.PlanHash));
    }

    [Fact]
    public void ObservationCreateAcceptsAnObservedTimeAtOrBeforeNowWithinTolerance()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var providerId = PurviewProviderOperationId.Create("purview-job-123");

        var observation = PurviewImportJobObservation.Create(
            Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated, Now.AddMinutes(-1), "operator@contoso.com", Now);

        Assert.Equal(PurviewImportJobObservedStatus.JobCreated, observation.ObservedStatus);
    }

    [Fact]
    public void ObservationCreateFailsClosedWhenObservedAtIsBeforeTheEarliestPlausibleProductOperation()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var providerId = PurviewProviderOperationId.Create("purview-job-123");

        Assert.Throws<PurviewImportJobPrerequisiteException>(() =>
            PurviewImportJobObservation.Create(
                Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated,
                new DateTimeOffset(2019, 12, 31, 0, 0, 0, TimeSpan.Zero), "operator@contoso.com", Now));
    }

    [Fact]
    public void ObservationCreateFailsClosedWhenObservedAtIsFarInTheFutureRelativeToRecording()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var providerId = PurviewProviderOperationId.Create("purview-job-123");

        Assert.Throws<PurviewImportJobPrerequisiteException>(() =>
            PurviewImportJobObservation.Create(
                Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated,
                Now.AddHours(1), "operator@contoso.com", Now));
    }

    [Fact]
    public void ObservationRehydrateFailsClosedWhenTheProviderOperationIdWasTamperedAfterPersistence()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var providerId = PurviewProviderOperationId.Create("purview-job-123");
        var observation = PurviewImportJobObservation.Create(
            Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated, Now, "operator@contoso.com", Now);
        var tamperedProviderId = PurviewProviderOperationId.Create("purview-job-DIFFERENT");

        Assert.Throws<PurviewImportJobIntegrityViolationException>(() =>
            PurviewImportJobObservation.Rehydrate(
                observation.Tenant, observation.Project, observation.Wave, observation.PlannedJobName, tamperedProviderId,
                observation.ObservedStatus, observation.ObservedAtUtc, observation.OperatorLabel, observation.RecordedAtUtc, observation.ObservationHash));
    }

    [Fact]
    public void SameLogicalObservationIgnoresRecordedAtAndOperatorLabelForReplayConvergence()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var providerId = PurviewProviderOperationId.Create("purview-job-123");
        var first = PurviewImportJobObservation.Create(
            Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated, Now, "operator-a@contoso.com", Now);
        var replay = PurviewImportJobObservation.Create(
            Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated, Now, "operator-b@contoso.com", Now.AddMinutes(5));

        Assert.True(first.IsSameLogicalObservationAs(replay));
    }

    [Fact]
    public void DifferentObservedStatusIsNeverTheSameLogicalObservation()
    {
        var name = PurviewImportJobName.Compute(Tenant, Project, Wave, 1);
        var providerId = PurviewProviderOperationId.Create("purview-job-123");
        var created = PurviewImportJobObservation.Create(
            Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.JobCreated, Now, "operator@contoso.com", Now);
        var analysisCompleted = PurviewImportJobObservation.Create(
            Tenant, Project, Wave, name, providerId, PurviewImportJobObservedStatus.AnalysisCompleted, Now, "operator@contoso.com", Now);

        Assert.False(created.IsSameLogicalObservationAs(analysisCompleted));
    }
}
