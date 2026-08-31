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
/// AB-I8-004 — <see cref="ApproveCanaryFirstWaveUseCase"/>: bloqueio estrutural enquanto qualquer outro
/// cenário obrigatório não está Pass (escopo obrigatório item 11), RBAC restrito a Administrator/Approver.
/// </summary>
public sealed class ApproveCanaryFirstWaveUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));

    private static async Task<InMemoryCanaryScenarioResultStore> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, SomeHash, Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now, CancellationToken.None);
        return new InMemoryCanaryScenarioResultStore(planStore);
    }

    private static async Task PassEveryOtherScenarioAsync(InMemoryCanaryScenarioResultStore resultStore)
    {
        foreach (var definition in CanaryScenarioCatalog.AllScenarios)
        {
            if (definition.Id == CanaryScenarioCatalog.FirstWaveApprovalScenarioId)
            {
                continue;
            }

            await resultStore.RecordResultAsync(
                Scope, 1, definition.Id, CanaryScenarioStatus.Pass, CanaryEvidenceReference.SystemDerived(SomeHash, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now, "svc-canary", "ServiceAccount", CorrelationId.New(), Now, CancellationToken.None);
        }
    }

    private static ApproveCanaryFirstWaveUseCase BuildUseCase(InMemoryCanaryScenarioResultStore resultStore, FakeAuthenticatedActorAccessor? actor = null) =>
        new(resultStore, new FixedClock(Now), actor ?? new FakeAuthenticatedActorAccessor("approver-1", PortalRoles.Approver));

    [Fact]
    public async Task ApprovalIsBlockedWhenNoOtherScenarioHasBeenSubmitted()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(resultStore);

        var ex = await Assert.ThrowsAsync<CanaryFirstWaveApprovalBlockedException>(
            () => useCase.ExecuteAsync(new ApproveCanaryFirstWaveCommand(Scope, 1, Notes: null, CorrelationId.New()), CancellationToken.None));

        Assert.Contains("9 cenário", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovalIsBlockedWhenASingleOtherScenarioIsStillNotPass()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        await PassEveryOtherScenarioAsync(resultStore);

        // Regride um cenário para Fail — a aprovação deve continuar bloqueada mesmo com todos os outros oito Pass.
        var regressed = CanaryScenarioCatalog.AllScenarios.First(d => d.Id.Value == "CANARY.CRASH_RECOVERY");
        await resultStore.RecordResultAsync(
            Scope, 1, regressed.Id, CanaryScenarioStatus.Fail, CanaryEvidenceReference.SystemDerived(SomeHash, "regressed"),
            "CRASH_RECOVERY_REGRESSED", Now, "svc-canary", "ServiceAccount", CorrelationId.New(), Now, CancellationToken.None);

        var useCase = BuildUseCase(resultStore);

        await Assert.ThrowsAsync<CanaryFirstWaveApprovalBlockedException>(
            () => useCase.ExecuteAsync(new ApproveCanaryFirstWaveCommand(Scope, 1, Notes: null, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalSucceedsWhenAllNineOtherScenariosArePass()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        await PassEveryOtherScenarioAsync(resultStore);
        var useCase = BuildUseCase(resultStore);

        var result = await useCase.ExecuteAsync(new ApproveCanaryFirstWaveCommand(Scope, 1, "low-criticality first wave, approved", CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
        Assert.Equal(CanaryEvidenceKind.HumanApprovalDecision, result.Evidence.Kind);

        var evaluation = CanaryGateEvaluator.Evaluate(
            await resultStore.GetAllLatestForPlanAsync(Scope, 1, CancellationToken.None), Now);
        Assert.Equal(CanaryOutcome.CanaryPassed, evaluation.Outcome);
    }

    [Fact]
    public async Task AnOperatorCannotApproveTheFirstWave()
    {
        var resultStore = await SeedAuthorizedPlanAsync();
        await PassEveryOtherScenarioAsync(resultStore);
        var useCase = BuildUseCase(resultStore, new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

        await Assert.ThrowsAsync<CanaryAuthorizationException>(
            () => useCase.ExecuteAsync(new ApproveCanaryFirstWaveCommand(Scope, 1, Notes: null, CorrelationId.New()), CancellationToken.None));
    }
}
