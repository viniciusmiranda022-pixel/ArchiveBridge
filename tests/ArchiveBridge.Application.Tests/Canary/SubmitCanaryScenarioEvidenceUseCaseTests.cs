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
/// AB-I8-004 — <see cref="SubmitCanaryScenarioEvidenceUseCase"/>: bloqueio estrutural de atestação para
/// cenários SystemDerived/gate de aprovação, RBAC, drift do plano, e convergência idempotente.
/// </summary>
public sealed class SubmitCanaryScenarioEvidenceUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly CanaryScenarioId OperatorAttestedScenario = new("CANARY.CORPUS_ITEM_TYPE_DIVERSITY");
    private static readonly CanaryScenarioId SystemDerivedScenario = new("CANARY.CRASH_RECOVERY");

    private static SubmitCanaryScenarioEvidenceUseCase BuildUseCase(InMemoryCanaryScenarioResultStore resultStore, FakeAuthenticatedActorAccessor? actor = null) =>
        new(resultStore, new FixedClock(Now), actor ?? new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

    private static async Task<InMemoryCanaryScenarioResultStore> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, readinessReviewVersion: 1, new Sha256Hash(new string('a', 64)), Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", new Sha256Hash(new string('a', 64)), new Sha256Hash(new string('a', 64)),
            new Sha256Hash(new string('a', 64)), "approver-1", "Approver", CorrelationId.New(), Now, CancellationToken.None);
        return new InMemoryCanaryScenarioResultStore(planStore);
    }

    [Fact]
    public async Task SubmittingEvidenceForASystemDerivedScenarioIsRejected()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(resultStore);

        await Assert.ThrowsAsync<CanaryScenarioNotAttestableException>(() => useCase.ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(Scope, 1, SystemDerivedScenario, CanaryScenarioStatus.Pass, "I promise it passed", string.Empty, Now, CorrelationId.New()),
            CancellationToken.None));

        Assert.Null(await resultStore.GetLatestAsync(Scope, 1, SystemDerivedScenario, CancellationToken.None));
    }

    [Fact]
    public async Task SubmittingEvidenceForTheApprovalGateIsRejected()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(resultStore);

        await Assert.ThrowsAsync<CanaryScenarioNotAttestableException>(() => useCase.ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(
                Scope, 1, CanaryScenarioCatalog.FirstWaveApprovalScenarioId, CanaryScenarioStatus.Pass, "I approve", string.Empty, Now, CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task SubmittingValidEvidenceForAnOperatorAttestedScenarioSucceeds()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(resultStore);

        var result = await useCase.ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(
                Scope, 1, OperatorAttestedScenario, CanaryScenarioStatus.Pass, "20 item types observed in the canary corpus report v3",
                string.Empty, Now, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
        Assert.Equal(CanaryEvidenceKind.OperatorAttestation, result.Evidence.Kind);
    }

    [Fact]
    public async Task RepeatedIdenticalSubmissionsConvergeWithoutDuplicatingTheResult()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(resultStore);
        var command = new SubmitCanaryScenarioEvidenceCommand(
            Scope, 1, OperatorAttestedScenario, CanaryScenarioStatus.Pass, "same evidence text", string.Empty, Now, CorrelationId.New());

        await useCase.ExecuteAsync(command, CancellationToken.None);
        await useCase.ExecuteAsync(command with { Correlation = CorrelationId.New() }, CancellationToken.None);

        var history = await resultStore.GetHistoryAsync(Scope, 1, OperatorAttestedScenario, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task SubmittingAgainstASupersededPlanVersionIsRejected()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, new Sha256Hash(new string('a', 64)), Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", new Sha256Hash(new string('a', 64)), new Sha256Hash(new string('a', 64)),
            new Sha256Hash(new string('a', 64)), "approver-1", "Approver", CorrelationId.New(), Now, CancellationToken.None);
        var resultStore = new InMemoryCanaryScenarioResultStore(planStore);
        var useCase = BuildUseCase(resultStore);

        // Nova revisão de readiness -> nova versão do plano (drift).
        await planStore.AuthorizeAsync(
            Scope, 2, new Sha256Hash(new string('b', 64)), Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", new Sha256Hash(new string('a', 64)), new Sha256Hash(new string('a', 64)),
            new Sha256Hash(new string('a', 64)), "approver-1", "Approver", CorrelationId.New(), Now, CancellationToken.None);

        await Assert.ThrowsAsync<CanaryPlanSupersededException>(() => useCase.ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(Scope, 1, OperatorAttestedScenario, CanaryScenarioStatus.Pass, "evidence", string.Empty, Now, CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task AViewerCannotSubmitEvidence()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        var useCase = new SubmitCanaryScenarioEvidenceUseCase(
            resultStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("viewer-1", PortalRoles.Viewer));

        await Assert.ThrowsAsync<CanaryAuthorizationException>(() => useCase.ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(Scope, 1, OperatorAttestedScenario, CanaryScenarioStatus.Pass, "evidence", string.Empty, Now, CorrelationId.New()),
            CancellationToken.None));
    }
}
