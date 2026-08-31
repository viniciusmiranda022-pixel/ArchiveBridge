using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests.ProductionReadiness;

/// <summary>
/// AB-I8-001 — <see cref="ProductionReadinessReviewSnapshot"/>: composição pura, convergência determinística
/// por <see cref="ProductionReadinessReviewSnapshot.ReviewFingerprint"/>, e tamper-evidence de
/// <see cref="ProductionReadinessReviewSnapshot.Rehydrate"/> (incluindo a revalidação cruzada de
/// outcome/blockers contra as linhas de controle carregadas — não só o hash bruto).
/// </summary>
public sealed class ProductionReadinessReviewSnapshotTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ComposingWithNoResolvedControlsProducesNotReady()
    {
        var snapshot = Compose(reviewVersion: 1, new Dictionary<ReadinessControlId, ReadinessControlResult>());

        Assert.Equal(ProductionReadinessOutcome.NotReady, snapshot.Outcome);
        Assert.Equal(ReadinessControlCatalog.AllControls.Count, snapshot.Blockers.Count);
        Assert.Equal(ReadinessControlCatalog.AllControls.Count, snapshot.ControlResults.Count);
    }

    [Fact]
    public void ComposingWithEveryControlPassingProducesReadyForCanary()
    {
        var snapshot = Compose(reviewVersion: 1, AllControlsPassing());

        Assert.Equal(ProductionReadinessOutcome.ReadyForCanary, snapshot.Outcome);
        Assert.Empty(snapshot.Blockers);
    }

    [Fact]
    public void AnInvalidCommitShaIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Compose(reviewVersion: 1, AllControlsPassing(), commitSha: "not-a-sha"));
    }

    [Fact]
    public void ReviewFingerprintIsIndependentOfVersionTimestampAndActor()
    {
        var resolved = AllControlsPassing();

        var first = ProductionReadinessReviewSnapshot.Compose(
            Tenant, Project, reviewVersion: 1, ValidCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved,
            "actor-a", "Approver", Correlation, Now);
        var second = ProductionReadinessReviewSnapshot.Compose(
            Tenant, Project, reviewVersion: 7, ValidCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved,
            "actor-b", "Administrator", new CorrelationId(Guid.NewGuid()), Now + TimeSpan.FromHours(3));

        Assert.Equal(first.ReviewFingerprint, second.ReviewFingerprint);
    }

    [Fact]
    public void AMaterialChangeInEvidenceProducesADifferentReviewFingerprint()
    {
        var passing = Compose(reviewVersion: 1, AllControlsPassing());

        var resolvedWithOneBlocked = AllControlsPassing();
        var blockedId = new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");
        resolvedWithOneBlocked[blockedId] = ReadinessControlResult.Create(
            blockedId, ReadinessGateGroup.Security, ReadinessControlStatus.NotPerformed,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "pentest-readiness:v1"), "PENTEST_NOT_PERFORMED", Now);
        var blocked = Compose(reviewVersion: 1, resolvedWithOneBlocked);

        Assert.NotEqual(passing.ReviewFingerprint, blocked.ReviewFingerprint);
    }

    [Fact]
    public void RehydratingAnUntamperedSnapshotSucceeds()
    {
        var original = Compose(reviewVersion: 3, AllControlsPassing());

        var rehydrated = ProductionReadinessReviewSnapshot.Rehydrate(
            original.Tenant, original.Project, original.ReviewVersion, original.BuildCommitSha, original.BuildArtifactDigest,
            original.PolicyVersionFingerprint, original.CapabilityMatrixFingerprint, original.ControlResults, original.Outcome,
            original.Blockers, original.ReviewFingerprint, original.SubmittedBy, original.SubmittedByRole, original.Correlation,
            original.GeneratedAtUtc, original.SchemaVersion, original.SnapshotHash);

        Assert.Equal(original.SnapshotHash, rehydrated.SnapshotHash);
        Assert.Equal(original.Outcome, rehydrated.Outcome);
    }

    [Fact]
    public void RehydratingWithATamperedControlResultIsRejectedByTheReviewFingerprintCheck()
    {
        var original = Compose(reviewVersion: 1, AllControlsPassing());

        // Adultera uma linha de controle DEPOIS de computar o review_fingerprint original — simula um INSERT
        // direto/corrompido fora do caminho de escrita.
        var tamperedControlId = new ReadinessControlId("DATA.PRIVACY_IMPACT_ASSESSMENT");
        var tamperedResults = original.ControlResults
            .Select(result => result.ControlId == tamperedControlId
                ? ReadinessControlResult.Create(
                    result.ControlId, result.Group, ReadinessControlStatus.Fail, result.Evidence, "TAMPERED", result.ObservedAtUtc)
                : result)
            .ToList();

        Assert.Throws<ProductionReadinessIntegrityViolationException>(() =>
            ProductionReadinessReviewSnapshot.Rehydrate(
                original.Tenant, original.Project, original.ReviewVersion, original.BuildCommitSha, original.BuildArtifactDigest,
                original.PolicyVersionFingerprint, original.CapabilityMatrixFingerprint, tamperedResults, original.Outcome,
                original.Blockers, original.ReviewFingerprint, original.SubmittedBy, original.SubmittedByRole, original.Correlation,
                original.GeneratedAtUtc, original.SchemaVersion, original.SnapshotHash));
    }

    [Fact]
    public void RehydratingWithATamperedOutcomeColumnIsRejectedEvenWhenTheReviewFingerprintStillMatches()
    {
        // Simula uma linha de header cuja coluna outcome foi adulterada para ReadyForCanary SEM alterar
        // NENHUMA linha de controle (então review_fingerprint recomputado ainda bate) — a defesa cruzada
        // contra as linhas de controle carregadas precisa, sozinha, detectar isto.
        var resolved = AllControlsPassing();
        var blockedId = new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");
        resolved[blockedId] = ReadinessControlResult.Create(
            blockedId, ReadinessGateGroup.Security, ReadinessControlStatus.NotPerformed,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "pentest-readiness:v1"), "PENTEST_NOT_PERFORMED", Now);
        var original = Compose(reviewVersion: 1, resolved);
        Assert.Equal(ProductionReadinessOutcome.NotReady, original.Outcome);

        Assert.Throws<ProductionReadinessIntegrityViolationException>(() =>
            ProductionReadinessReviewSnapshot.Rehydrate(
                original.Tenant, original.Project, original.ReviewVersion, original.BuildCommitSha, original.BuildArtifactDigest,
                original.PolicyVersionFingerprint, original.CapabilityMatrixFingerprint, original.ControlResults,
                ProductionReadinessOutcome.ReadyForCanary, persistedBlockers: [], original.ReviewFingerprint, original.SubmittedBy,
                original.SubmittedByRole, original.Correlation, original.GeneratedAtUtc, original.SchemaVersion, original.SnapshotHash));
    }

    [Fact]
    public void ComposeNeverProducesReadyForCanaryWithAnyBlockerPresent()
    {
        // Defesa em profundidade: mesmo variando aleatoriamente quais controles passam, ReadyForCanary só
        // aparece com Blockers vazio.
        var resolved = AllControlsPassing();
        var random = new Random(Seed: 42);
        var toBlock = ReadinessControlCatalog.AllControls[random.Next(ReadinessControlCatalog.AllControls.Count)].Id;
        var definition = ReadinessControlCatalog.Definition(toBlock);
        resolved[toBlock] = ReadinessControlResult.NotMeasured(toBlock, definition.Group, "TEST_BLOCK", Now);

        var snapshot = Compose(reviewVersion: 1, resolved);

        if (snapshot.Outcome == ProductionReadinessOutcome.ReadyForCanary)
        {
            Assert.Empty(snapshot.Blockers);
        }
        else
        {
            Assert.NotEmpty(snapshot.Blockers);
        }
    }

    private static ProductionReadinessReviewSnapshot Compose(
        int reviewVersion, Dictionary<ReadinessControlId, ReadinessControlResult> resolved, string commitSha = ValidCommitSha) =>
        ProductionReadinessReviewSnapshot.Compose(
            Tenant, Project, reviewVersion, commitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved,
            "svc-readiness", "Approver", Correlation, Now);

    private static Dictionary<ReadinessControlId, ReadinessControlResult> AllControlsPassing()
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
