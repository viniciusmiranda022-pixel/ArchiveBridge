using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Canary;

/// <summary>
/// AB-I8-004 — <see cref="CanaryPlan"/>: gate de entrada estruturalmente inalcançável sem ReadyForCanary,
/// convergência idempotente de <see cref="CanaryPlan.PlanFingerprint"/>, e revalidação fail-closed de
/// integridade em <see cref="CanaryPlan.Rehydrate"/>.
/// </summary>
public sealed class CanaryPlanTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly Sha256Hash OtherHash = new(new string('b', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static CanaryPlan ComposeValid(int planVersion = 1, Sha256Hash? readinessFingerprint = null) =>
        CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion, readinessReviewVersion: 1, readinessFingerprint ?? SomeHash,
            ProductionReadinessOutcome.ReadyForCanary, ValidCommitSha, SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now);

    [Fact]
    public void ComposeThrowsWhenReadinessOutcomeIsNotReadyForCanary()
    {
        var ex = Assert.Throws<CanaryEntryGateBlockedException>(() => CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion: 1, readinessReviewVersion: 1, SomeHash,
            ProductionReadinessOutcome.NotReady, ValidCommitSha, SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now));

        Assert.Contains("ReadyForCanary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeThrowsForAnInvalidCommitSha()
    {
        Assert.Throws<ArgumentException>(() => CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion: 1, readinessReviewVersion: 1, SomeHash,
            ProductionReadinessOutcome.ReadyForCanary, "not-a-sha", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now));
    }

    [Fact]
    public void ComposeThrowsForANonPositivePlanVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion: 0, readinessReviewVersion: 1, SomeHash,
            ProductionReadinessOutcome.ReadyForCanary, ValidCommitSha, SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now));
    }

    [Fact]
    public void PlanFingerprintIsIndependentOfPlanIdVersionActorAndTimestamp()
    {
        var first = CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion: 1, readinessReviewVersion: 1, SomeHash,
            ProductionReadinessOutcome.ReadyForCanary, ValidCommitSha, SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now);

        var second = CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion: 7, readinessReviewVersion: 1, SomeHash,
            ProductionReadinessOutcome.ReadyForCanary, ValidCommitSha, SomeHash, SomeHash, SomeHash, "approver-2", "Administrator",
            CorrelationId.New(), Now.AddDays(3));

        Assert.Equal(first.PlanFingerprint.Value, second.PlanFingerprint.Value);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void PlanFingerprintChangesWhenAnyRealBindingFactChanges(bool changeReadiness, bool changeDigest, bool changePolicy, bool changeCapability)
    {
        var baseline = ComposeValid();

        var candidate = CanaryPlan.Compose(
            Tenant, Project, CanaryPlanId.New(), planVersion: 1, readinessReviewVersion: 1,
            changeReadiness ? OtherHash : SomeHash, ProductionReadinessOutcome.ReadyForCanary, ValidCommitSha,
            changeDigest ? OtherHash : SomeHash, changePolicy ? OtherHash : SomeHash, changeCapability ? OtherHash : SomeHash,
            "approver-1", "Approver", CorrelationId.New(), Now);

        Assert.NotEqual(baseline.PlanFingerprint.Value, candidate.PlanFingerprint.Value);
    }

    [Fact]
    public void RehydrateRoundTripsAValidPlan()
    {
        var composed = ComposeValid();

        var rehydrated = CanaryPlan.Rehydrate(
            composed.Tenant, composed.Project, composed.PlanId, composed.PlanVersion, composed.ReadinessReviewVersion,
            composed.ReadinessReviewFingerprint, composed.BuildCommitSha, composed.BuildArtifactDigest,
            composed.PolicyVersionFingerprint, composed.CapabilityMatrixFingerprint, composed.PlanFingerprint, composed.AuthorizedBy,
            composed.AuthorizedByRole, composed.Correlation, composed.AuthorizedAtUtc, composed.SchemaVersion, composed.PlanHash);

        Assert.Equal(composed.PlanHash.Value, rehydrated.PlanHash.Value);
        Assert.Equal(composed.PlanId, rehydrated.PlanId);
    }

    [Fact]
    public void RehydrateThrowsWhenThePersistedPlanFingerprintDoesNotMatchTheRecomputedOne()
    {
        var composed = ComposeValid();
        var tamperedFingerprint = OtherHash;

        Assert.Throws<CanaryIntegrityViolationException>(() => CanaryPlan.Rehydrate(
            composed.Tenant, composed.Project, composed.PlanId, composed.PlanVersion, composed.ReadinessReviewVersion,
            composed.ReadinessReviewFingerprint, composed.BuildCommitSha, composed.BuildArtifactDigest,
            composed.PolicyVersionFingerprint, composed.CapabilityMatrixFingerprint, tamperedFingerprint, composed.AuthorizedBy,
            composed.AuthorizedByRole, composed.Correlation, composed.AuthorizedAtUtc, composed.SchemaVersion, composed.PlanHash));
    }

    [Fact]
    public void RehydrateThrowsWhenThePersistedPlanHashDoesNotMatchTheRecomputedOne()
    {
        var composed = ComposeValid();
        var tamperedHash = OtherHash;

        Assert.Throws<CanaryIntegrityViolationException>(() => CanaryPlan.Rehydrate(
            composed.Tenant, composed.Project, composed.PlanId, composed.PlanVersion, composed.ReadinessReviewVersion,
            composed.ReadinessReviewFingerprint, composed.BuildCommitSha, composed.BuildArtifactDigest,
            composed.PolicyVersionFingerprint, composed.CapabilityMatrixFingerprint, composed.PlanFingerprint, composed.AuthorizedBy,
            composed.AuthorizedByRole, composed.Correlation, composed.AuthorizedAtUtc, composed.SchemaVersion, tamperedHash));
    }
}
