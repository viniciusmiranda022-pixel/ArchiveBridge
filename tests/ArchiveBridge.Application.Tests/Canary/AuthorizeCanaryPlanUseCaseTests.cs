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
/// AB-I8-004 — <see cref="AuthorizeCanaryPlanUseCase"/>: gate de entrada estruturalmente inalcançável sem um
/// Production Readiness Review canônico e vigente ReadyForCanary, RBAC server-side, e convergência
/// idempotente sob replay.
/// </summary>
public sealed class AuthorizeCanaryPlanUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static AuthorizeCanaryPlanUseCase BuildUseCase(
        InMemoryProductionReadinessReviewStore readinessStore, InMemoryCanaryPlanStore planStore, FakeAuthenticatedActorAccessor? actor = null) =>
        new(readinessStore, planStore, new FixedClock(Now), actor ?? new FakeAuthenticatedActorAccessor("approver-1", PortalRoles.Approver));

    [Fact]
    public async Task AuthorizeThrowsWhenNoReadinessReviewHasEverBeenComposed()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        var planStore = new InMemoryCanaryPlanStore();
        var useCase = BuildUseCase(readinessStore, planStore);

        await Assert.ThrowsAsync<CanaryEntryGateBlockedException>(
            () => useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await planStore.GetLatestAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizeThrowsWhenTheReadinessReviewOutcomeIsNotReadyForCanary()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        readinessStore.Seed(Scope, BuildNotReadySnapshot());
        var planStore = new InMemoryCanaryPlanStore();
        var useCase = BuildUseCase(readinessStore, planStore);

        await Assert.ThrowsAsync<CanaryEntryGateBlockedException>(
            () => useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await planStore.GetLatestAsync(Scope, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizeSucceedsWhenTheReadinessReviewIsReadyForCanary()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        readinessStore.Seed(Scope, ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now));
        var planStore = new InMemoryCanaryPlanStore();
        var useCase = BuildUseCase(readinessStore, planStore);

        var plan = await useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(1, plan.PlanVersion);
        Assert.Equal(ValidCommitSha, plan.BuildCommitSha);
    }

    [Fact]
    public async Task RepeatedAuthorizationWithTheSameReadinessConvergesToTheSamePlanVersion()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        readinessStore.Seed(Scope, ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now));
        var planStore = new InMemoryCanaryPlanStore();
        var useCase = BuildUseCase(readinessStore, planStore);

        var first = await useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(first.PlanVersion, second.PlanVersion);
        Assert.Equal(first.PlanId, second.PlanId);
    }

    [Fact]
    public async Task DriftInTheReadinessReviewProducesANewPlanVersionPreservingThePlanIdentity()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        readinessStore.Seed(Scope, ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now));
        var planStore = new InMemoryCanaryPlanStore();
        var useCase = BuildUseCase(readinessStore, planStore);
        var before = await useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None);

        // Drift: nova revisão de readiness com versão diferente (mesmo conteúdo agregado, mas o
        // ReviewVersion/ReviewFingerprint mudam porque é uma composição/ator diferente).
        readinessStore.Seed(Scope, ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 2, ValidCommitSha, CorrelationId.New(), Now.AddHours(1)));

        var after = await useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None);

        Assert.True(after.PlanVersion > before.PlanVersion);
        Assert.Equal(before.PlanId, after.PlanId);
    }

    [Fact]
    public async Task AnonymousActorIsRejectedBeforeAnyDataAccess()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        var planStore = new InMemoryCanaryPlanStore();
        var unauthenticatedUseCase = new AuthorizeCanaryPlanUseCase(readinessStore, planStore, new FixedClock(Now), new UnauthenticatedActorAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unauthenticatedUseCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task AViewerCannotAuthorizeAPlan()
    {
        var readinessStore = new InMemoryProductionReadinessReviewStore();
        readinessStore.Seed(Scope, ReadyForCanaryReadinessFixture.Build(Scope.Tenant, Scope.Project, 1, ValidCommitSha, CorrelationId.New(), Now));
        var planStore = new InMemoryCanaryPlanStore();
        var useCase = new AuthorizeCanaryPlanUseCase(
            readinessStore, planStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("viewer-1", PortalRoles.Viewer));

        await Assert.ThrowsAsync<CanaryAuthorizationException>(
            () => useCase.ExecuteAsync(new AuthorizeCanaryPlanCommand(Scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await planStore.GetLatestAsync(Scope, CancellationToken.None));
    }

    private static Domain.ProductionReadiness.ProductionReadinessReviewSnapshot BuildNotReadySnapshot() =>
        Domain.ProductionReadiness.ProductionReadinessReviewSnapshot.Compose(
            Scope.Tenant, Scope.Project, reviewVersion: 1, ValidCommitSha, new Sha256Hash(new string('a', 64)),
            new Sha256Hash(new string('a', 64)), new Sha256Hash(new string('a', 64)),
            new Dictionary<Domain.ProductionReadiness.ReadinessControlId, Domain.ProductionReadiness.ReadinessControlResult>(),
            "svc-readiness", "Administrator", CorrelationId.New(), Now);
}
