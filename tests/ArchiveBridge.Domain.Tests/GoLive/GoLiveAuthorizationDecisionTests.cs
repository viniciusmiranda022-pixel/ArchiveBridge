using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests.GoLive;

/// <summary>
/// AB-I8-010 — <see cref="GoLiveAuthorizationDecision"/>: convergência idempotente de
/// <see cref="GoLiveAuthorizationDecision.AuthorizationFingerprint"/>, revalidação fail-closed de integridade
/// em <see cref="GoLiveAuthorizationDecision.Rehydrate"/>, e <see cref="GoLiveOutcome"/> NUNCA representa
/// migração <c>Completed</c> (o enum deliberadamente não possui esse valor).
/// </summary>
public sealed class GoLiveAuthorizationDecisionTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly Sha256Hash OtherHash = new(new string('b', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void GoLiveOutcomeNeverRepresentsCompleted()
    {
        var values = Enum.GetNames<GoLiveOutcome>();
        Assert.Equal(2, values.Length);
        Assert.DoesNotContain(values, name => name.Contains("Completed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuthorizationFingerprintIsIndependentOfIdentityVersionActorAndTimestamp()
    {
        var operationalResults = AllOperationalControlsPassing();

        var first = GoLiveAuthorizationDecision.Compose(
            Tenant, Project, GoLiveAuthorizationId.New(), 1, CanaryPlanId.New(), 3, SomeHash, 3, SomeHash, ValidCommitSha,
            SomeHash, SomeHash, SomeHash, CanaryOutcome.CanaryPassed, 3, SomeHash, operationalResults, "approver-1",
            "Approver", CorrelationId.New(), Now);

        var second = GoLiveAuthorizationDecision.Compose(
            Tenant, Project, GoLiveAuthorizationId.New(), 7, first.CanaryPlanId, 3, SomeHash, 3, SomeHash, ValidCommitSha,
            SomeHash, SomeHash, SomeHash, CanaryOutcome.CanaryPassed, 3, SomeHash, operationalResults, "approver-2",
            "Administrator", CorrelationId.New(), Now.AddDays(2));

        Assert.Equal(first.AuthorizationFingerprint.Value, second.AuthorizationFingerprint.Value);
    }

    [Fact]
    public void AuthorizationFingerprintChangesWhenCanaryOutcomeChanges()
    {
        var operationalResults = AllOperationalControlsPassing();
        var canaryPlanId = CanaryPlanId.New();

        var passed = GoLiveAuthorizationDecision.Compose(
            Tenant, Project, GoLiveAuthorizationId.New(), 1, canaryPlanId, 3, SomeHash, 3, SomeHash, ValidCommitSha,
            SomeHash, SomeHash, SomeHash, CanaryOutcome.CanaryPassed, 3, SomeHash, operationalResults, "approver-1",
            "Approver", CorrelationId.New(), Now);

        var notPassed = GoLiveAuthorizationDecision.Compose(
            Tenant, Project, GoLiveAuthorizationId.New(), 1, canaryPlanId, 3, SomeHash, 3, SomeHash, ValidCommitSha,
            SomeHash, SomeHash, SomeHash, CanaryOutcome.NotPassed, 3, SomeHash, operationalResults, "approver-1",
            "Approver", CorrelationId.New(), Now);

        Assert.NotEqual(passed.AuthorizationFingerprint.Value, notPassed.AuthorizationFingerprint.Value);
        Assert.Equal(GoLiveOutcome.GoLiveAuthorized, passed.Outcome);
        Assert.Equal(GoLiveOutcome.Blocked, notPassed.Outcome);
    }

    [Fact]
    public void RehydrateThrowsWhenAuthorizationFingerprintIsTampered()
    {
        var record = ComposeGoLiveAuthorized();

        var ex = Assert.Throws<GoLiveIntegrityViolationException>(() => GoLiveAuthorizationDecision.Rehydrate(
            record.Tenant, record.Project, record.AuthorizationId, record.AuthorizationVersion, record.CanaryPlanId,
            record.CanaryPlanVersion, record.CanaryPlanFingerprint, record.ReadinessReviewVersion, record.ReadinessReviewFingerprint,
            record.BuildCommitSha, record.BuildArtifactDigest, record.PolicyVersionFingerprint, record.CapabilityMatrixFingerprint,
            record.CanaryOutcomeAtAuthorization, record.CurrentReadinessReviewVersionAtAuthorization,
            record.CurrentReadinessReviewFingerprintAtAuthorization, record.OperationalControlResults, record.Outcome,
            OtherHash /* fingerprint tampered */, record.AuthorizedBy, record.AuthorizedByRole, record.Correlation,
            record.AuthorizedAtUtc, record.SchemaVersion, record.AuthorizationHash));

        Assert.Contains("authorization_fingerprint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RehydrateThrowsWhenOutcomeIsTamperedWithoutTouchingTheOperationalResults()
    {
        var record = ComposeGoLiveAuthorized();

        // Adultera SOMENTE o outcome (Blocked em vez de GoLiveAuthorized) sem tocar nas linhas de controle
        // operacionais reais — Rehydrate deve recomputar e recusar a divergência mesmo sem alterar fingerprint.
        var ex = Assert.Throws<GoLiveIntegrityViolationException>(() => GoLiveAuthorizationDecision.Rehydrate(
            record.Tenant, record.Project, record.AuthorizationId, record.AuthorizationVersion, record.CanaryPlanId,
            record.CanaryPlanVersion, record.CanaryPlanFingerprint, record.ReadinessReviewVersion, record.ReadinessReviewFingerprint,
            record.BuildCommitSha, record.BuildArtifactDigest, record.PolicyVersionFingerprint, record.CapabilityMatrixFingerprint,
            record.CanaryOutcomeAtAuthorization, record.CurrentReadinessReviewVersionAtAuthorization,
            record.CurrentReadinessReviewFingerprintAtAuthorization, record.OperationalControlResults,
            GoLiveOutcome.Blocked /* outcome tampered */, record.AuthorizationFingerprint, record.AuthorizedBy,
            record.AuthorizedByRole, record.Correlation, record.AuthorizedAtUtc, record.SchemaVersion, record.AuthorizationHash));

        Assert.Contains("outcome", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RehydrateRoundTripsSuccessfullyForAnUntamperedRecord()
    {
        var record = ComposeGoLiveAuthorized();

        var rehydrated = GoLiveAuthorizationDecision.Rehydrate(
            record.Tenant, record.Project, record.AuthorizationId, record.AuthorizationVersion, record.CanaryPlanId,
            record.CanaryPlanVersion, record.CanaryPlanFingerprint, record.ReadinessReviewVersion, record.ReadinessReviewFingerprint,
            record.BuildCommitSha, record.BuildArtifactDigest, record.PolicyVersionFingerprint, record.CapabilityMatrixFingerprint,
            record.CanaryOutcomeAtAuthorization, record.CurrentReadinessReviewVersionAtAuthorization,
            record.CurrentReadinessReviewFingerprintAtAuthorization, record.OperationalControlResults, record.Outcome,
            record.AuthorizationFingerprint, record.AuthorizedBy, record.AuthorizedByRole, record.Correlation,
            record.AuthorizedAtUtc, record.SchemaVersion, record.AuthorizationHash);

        Assert.Equal(GoLiveOutcome.GoLiveAuthorized, rehydrated.Outcome);
        Assert.Empty(rehydrated.Blockers);
    }

    private static GoLiveAuthorizationDecision ComposeGoLiveAuthorized() =>
        GoLiveAuthorizationDecision.Compose(
            Tenant, Project, GoLiveAuthorizationId.New(), 1, CanaryPlanId.New(), 3, SomeHash, 3, SomeHash, ValidCommitSha,
            SomeHash, SomeHash, SomeHash, CanaryOutcome.CanaryPassed, 3, SomeHash, AllOperationalControlsPassing(),
            "approver-1", "Approver", CorrelationId.New(), Now);

    private static Dictionary<ReadinessControlId, ReadinessControlResult> AllOperationalControlsPassing()
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in GoLiveGateEvaluator.OperationalControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeHash, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
