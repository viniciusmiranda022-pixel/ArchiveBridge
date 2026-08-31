using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests.Canary;

/// <summary>
/// AB-I8-004 — <see cref="GetCanaryPlanReportUseCase"/>: <c>null</c> quando nenhum plano ainda autorizado,
/// e <c>IsPromotable</c> só verdadeiro quando CanaryPassed E nenhum drift do Production Readiness Review
/// desde a autorização (escopo obrigatório item 5).
/// </summary>
public sealed class GetCanaryPlanReportUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static GetCanaryPlanReportUseCase BuildUseCase(
        InMemoryCanaryPlanStore planStore, InMemoryCanaryScenarioResultStore resultStore, InMemoryProductionReadinessReviewStore readinessStore) =>
        new(planStore, resultStore, readinessStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("viewer-1", PortalRoles.Viewer));

    [Fact]
    public async Task ReturnsNullWhenNoPlanHasBeenAuthorized()
    {
        var useCase = BuildUseCase(new InMemoryCanaryPlanStore(), new InMemoryCanaryScenarioResultStore(new InMemoryCanaryPlanStore()), new InMemoryProductionReadinessReviewStore());

        var report = await useCase.ExecuteAsync(new GetCanaryPlanReportQuery(Scope), CancellationToken.None);

        Assert.Null(report);
    }

    [Fact]
    public async Task IsNotPromotableWhileScenariosAreStillPending()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        var readiness = ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now);
        readinessStore.Seed(Scope, readiness);
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, readiness.ReviewVersion, readiness.ReviewFingerprint, readiness.Outcome, readiness.BuildCommitSha,
            readiness.BuildArtifactDigest, readiness.PolicyVersionFingerprint, readiness.CapabilityMatrixFingerprint,
            "approver-1", "Approver", CorrelationId.New(), Now, CancellationToken.None);
        var resultStore = new InMemoryCanaryScenarioResultStore(planStore);
        var useCase = BuildUseCase(planStore, resultStore, readinessStore);

        var report = await useCase.ExecuteAsync(new GetCanaryPlanReportQuery(Scope), CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(CanaryOutcome.NotPassed, report!.Outcome);
        Assert.False(report.IsPromotable);
        Assert.False(report.ReadinessHasDrifted);
        Assert.Equal(10, report.Scenarios.Count);
    }

    [Fact]
    public async Task IsPromotableWhenAllScenariosPassAndReadinessHasNotDrifted()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        var readiness = ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now);
        readinessStore.Seed(Scope, readiness);
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, readiness.ReviewVersion, readiness.ReviewFingerprint, readiness.Outcome, readiness.BuildCommitSha,
            readiness.BuildArtifactDigest, readiness.PolicyVersionFingerprint, readiness.CapabilityMatrixFingerprint,
            "approver-1", "Approver", CorrelationId.New(), Now, CancellationToken.None);
        var resultStore = new InMemoryCanaryScenarioResultStore(planStore);
        foreach (var definition in CanaryScenarioCatalog.AllScenarios)
        {
            var evidence = definition.Id == CanaryScenarioCatalog.FirstWaveApprovalScenarioId
                ? CanaryEvidenceReference.ApprovalDecision(SomeHash, "canary-first-wave-approval:v1")
                : CanaryEvidenceReference.SystemDerived(SomeHash, $"fixture:{definition.Id.Value}");
            await resultStore.RecordResultAsync(
                Scope, 1, definition.Id, CanaryScenarioStatus.Pass, evidence, reasonCode: string.Empty, Now, "svc-canary", "ServiceAccount",
                CorrelationId.New(), Now, CancellationToken.None);
        }

        var useCase = BuildUseCase(planStore, resultStore, readinessStore);

        var report = await useCase.ExecuteAsync(new GetCanaryPlanReportQuery(Scope), CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(CanaryOutcome.CanaryPassed, report!.Outcome);
        Assert.True(report.IsPromotable);
        Assert.Empty(report.BlockerSummaries);
    }

    [Fact]
    public async Task ReadinessDriftAfterAuthorizationMakesTheReportNotPromotableEvenWithCanaryPassed()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        var readiness = ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now);
        readinessStore.Seed(Scope, readiness);
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, readiness.ReviewVersion, readiness.ReviewFingerprint, readiness.Outcome, readiness.BuildCommitSha,
            readiness.BuildArtifactDigest, readiness.PolicyVersionFingerprint, readiness.CapabilityMatrixFingerprint,
            "approver-1", "Approver", CorrelationId.New(), Now, CancellationToken.None);
        var resultStore = new InMemoryCanaryScenarioResultStore(planStore);
        foreach (var definition in CanaryScenarioCatalog.AllScenarios)
        {
            await resultStore.RecordResultAsync(
                Scope, 1, definition.Id, CanaryScenarioStatus.Pass, CanaryEvidenceReference.SystemDerived(SomeHash, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now, "svc-canary", "ServiceAccount", CorrelationId.New(), Now, CancellationToken.None);
        }

        // Readiness avança para uma nova versão APÓS a autorização do plano (drift) — o plano em si continua
        // versão 1 (nenhum novo canário foi autorizado ainda).
        readinessStore.Seed(Scope, ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 2, ValidCommitSha, CorrelationId.New(), Now.AddHours(2)));

        var useCase = BuildUseCase(planStore, resultStore, readinessStore);
        var report = await useCase.ExecuteAsync(new GetCanaryPlanReportQuery(Scope), CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(CanaryOutcome.CanaryPassed, report!.Outcome);
        Assert.True(report.ReadinessHasDrifted);
        Assert.False(report.IsPromotable);
    }
}
