using ArchiveBridge.Application.GoLive;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using Xunit;
// Reaproveita as fakes de infraestrutura compartilhada do Passo 1 (AB-I8-001) — InMemoryRecoveryReadinessStore/
// InMemoryMailboxPrecheckStore/InMemoryMappingValidationStore/InMemoryPurviewUploadAttemptStore/
// InMemoryReadinessControlAttestationStore/InMemoryProductionReadinessReviewStore — nunca duplica um mecanismo
// de evidência paralelo só para os testes deste Passo; Canary.* traz as fakes específicas de canário
// (AB-I8-004) via alias, para nunca colidir com os tipos homônimos (FixedClock/FakeAuthenticatedActorAccessor/
// UnauthenticatedActorAccessor) já trazidos sem qualificação pelo using acima.
using ArchiveBridge.Application.Tests.ProductionReadiness;
using Canary = ArchiveBridge.Application.Tests.Canary;

namespace ArchiveBridge.Application.Tests.GoLive;

/// <summary>
/// AB-I8-010 — <see cref="AuthorizeGoLiveUseCase"/>: RBAC server-side (nunca do payload), gate de entrada
/// estruturalmente inalcançável sem NENHUM plano de canário, canário não-passado bloqueia,
/// drift do Production Readiness Review vigente contra o vinculado pelo canário bloqueia, revalidação
/// operacional FRESCA (não a cacheada no review original) bloqueia com evidência ausente, replay idêntico
/// converge, e GoLiveOutcome jamais representa a migração Completed.
/// </summary>
public sealed class AuthorizeGoLiveUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly AzCopyHomologationCatalog HomologatedCatalog =
        new([new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('b', 64)))]);

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private sealed class Fixtures
    {
        public InMemoryProductionReadinessReviewStore ReviewStore { get; } = new();
        public Canary.InMemoryCanaryPlanStore PlanStore { get; } = new();
        public Canary.InMemoryCanaryScenarioResultStore ResultStore { get; }
        public InMemoryRecoveryReadinessStore RecoveryStore { get; } = new();
        public InMemoryMailboxPrecheckStore MailboxStore { get; } = new();
        public InMemoryMappingValidationStore MappingStore { get; } = new();
        public InMemoryPurviewUploadAttemptStore UploadAttemptStore { get; } = new();
        public InMemoryReadinessControlAttestationStore AttestationStore { get; } = new();
        public InMemoryGoLiveAuthorizationStore AuthorizationStore { get; } = new();

        public Fixtures() => ResultStore = new Canary.InMemoryCanaryScenarioResultStore(PlanStore);

        public AuthorizeGoLiveUseCase BuildUseCase(Contracts.Abstractions.IAuthenticatedActorAccessor actorAccessor) =>
            new(
                PlanStore, ResultStore, ReviewStore, RecoveryStore, MailboxStore, MappingStore, UploadAttemptStore,
                HomologatedCatalog, AttestationStore, AuthorizationStore, new Canary.FixedClock(Now), actorAccessor);
    }

    private static async Task<ProductionReadinessReviewSnapshot> SeedReadyForCanaryAsync(
        InMemoryProductionReadinessReviewStore reviewStore, TenantScope scope, DateTimeOffset now)
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, now);
        }

        return await reviewStore.RecordReviewAsync(
            scope, ValidCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved, "svc-readiness", "Administrator",
            CorrelationId.New(), now, CancellationToken.None);
    }

    private static async Task<CanaryPlan> SeedAuthorizedCanaryPlanAsync(
        Canary.InMemoryCanaryPlanStore planStore, ProductionReadinessReviewSnapshot review, TenantScope scope, DateTimeOffset now) =>
        await planStore.AuthorizeAsync(
            scope, review.ReviewVersion, review.ReviewFingerprint, ProductionReadinessOutcome.ReadyForCanary, review.BuildCommitSha,
            review.BuildArtifactDigest, review.PolicyVersionFingerprint, review.CapabilityMatrixFingerprint, "approver-1", "Approver",
            CorrelationId.New(), now, CancellationToken.None);

    private static async Task MarkCanaryPassedAsync(
        Canary.InMemoryCanaryScenarioResultStore resultStore, TenantScope scope, CanaryPlan plan, DateTimeOffset now)
    {
        foreach (var scenario in CanaryScenarioCatalog.AllScenarios)
        {
            await resultStore.RecordResultAsync(
                scope, plan.PlanVersion, scenario.Id, CanaryScenarioStatus.Pass,
                CanaryEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{scenario.Id.Value}"), reasonCode: string.Empty, now,
                "operator-1", "Operator", CorrelationId.New(), now, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnonymousActorIsRejectedBeforeAnyScopedAccess()
    {
        var fixtures = new Fixtures();
        var useCase = fixtures.BuildUseCase(new Canary.UnauthenticatedActorAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync(new AuthorizeGoLiveCommand(NewScope(), CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task AViewerRoleCannotAuthorizeGoLive()
    {
        var fixtures = new Fixtures();
        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Viewer"));

        await Assert.ThrowsAsync<GoLiveAuthorizationException>(
            () => useCase.ExecuteAsync(new AuthorizeGoLiveCommand(NewScope(), CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task AnOperatorRoleCannotAuthorizeGoLive()
    {
        var fixtures = new Fixtures();
        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("bob", "Operator"));

        await Assert.ThrowsAsync<GoLiveAuthorizationException>(
            () => useCase.ExecuteAsync(new AuthorizeGoLiveCommand(NewScope(), CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task WithNoCanaryPlanAtAllTheEntryGateBlocksAndNothingIsPersisted()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));

        await Assert.ThrowsAsync<GoLiveEntryGateBlockedException>(
            () => useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await fixtures.AuthorizationStore.GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ACanaryPlanThatNeverPassedBlocksButIsStillPersistedForAudit()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var review = await SeedReadyForCanaryAsync(fixtures.ReviewStore, scope, Now);
        await SeedAuthorizedCanaryPlanAsync(fixtures.PlanStore, review, scope, Now);
        // Nenhum resultado de cenário é registrado — o canário permanece NotPassed.

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var decision = await useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(GoLiveOutcome.Blocked, decision.Outcome);
        Assert.Contains(decision.Blockers, blocker => blocker.Code == GoLiveBlocker.CanaryNotPassedCode);
        Assert.NotNull(await fixtures.AuthorizationStore.GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheCanaryPassedButNoOperationalEvidenceExistsGoLiveIsBlockedNotFabricated()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var review = await SeedReadyForCanaryAsync(fixtures.ReviewStore, scope, Now);
        var plan = await SeedAuthorizedCanaryPlanAsync(fixtures.PlanStore, review, scope, Now);
        await MarkCanaryPassedAsync(fixtures.ResultStore, scope, plan, Now);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var decision = await useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(GoLiveOutcome.Blocked, decision.Outcome);
        Assert.Equal(CanaryOutcome.CanaryPassed, decision.CanaryOutcomeAtAuthorization);
        Assert.DoesNotContain(decision.Blockers, blocker => blocker.Code == GoLiveBlocker.CanaryNotPassedCode);
        Assert.DoesNotContain(decision.Blockers, blocker => blocker.Code == GoLiveBlocker.ReadinessReviewDriftCode);

        // Os dois invariantes de policy M365 são auto-checagem pura (sem I/O) e passam de graça; ARCHIVE_LICENSE_QUOTA
        // permanece estruturalmente Blocked (nenhuma fonte canônica existe); todos os demais ficam NotMeasured
        // (nenhuma evidência real seedada) — GoLiveAuthorized nunca é fabricado por omissão.
        var targetRootPolicy = decision.OperationalControlResults.Single(r => r.ControlId.Value == "M365.TARGET_ROOT_POLICY");
        Assert.Equal(ReadinessControlStatus.Pass, targetRootPolicy.Status);
        var importLimits = decision.OperationalControlResults.Single(r => r.ControlId.Value == "M365.IMPORT_LIMITS_100GB_500ROWS");
        Assert.Equal(ReadinessControlStatus.Pass, importLimits.Status);
        var archiveLicenseQuota = decision.OperationalControlResults.Single(r => r.ControlId.Value == "M365.ARCHIVE_LICENSE_QUOTA");
        Assert.Equal(ReadinessControlStatus.Blocked, archiveLicenseQuota.Status);
        Assert.Contains(decision.Blockers, blocker => blocker.Code.Contains("M365.ARCHIVE_LICENSE_QUOTA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheReadinessReviewDriftsAfterTheCanaryGoLiveBlocksAsDrift()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var review = await SeedReadyForCanaryAsync(fixtures.ReviewStore, scope, Now);
        var plan = await SeedAuthorizedCanaryPlanAsync(fixtures.PlanStore, review, scope, Now);
        await MarkCanaryPassedAsync(fixtures.ResultStore, scope, plan, Now);

        // Um NOVO Production Readiness Review é composto DEPOIS do canário (build/digest diferente) — o
        // review vigente já não corresponde ao vinculado pelo plano de canário.
        var driftedResolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            driftedResolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        const string newCommitSha = "abcdef0123456789abcdef0123456789abcdef01";
        await fixtures.ReviewStore.RecordReviewAsync(
            scope, newCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, driftedResolved, "svc-readiness", "Administrator",
            CorrelationId.New(), Now.AddHours(1), CancellationToken.None);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var decision = await useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(GoLiveOutcome.Blocked, decision.Outcome);
        Assert.Contains(decision.Blockers, blocker => blocker.Code == GoLiveBlocker.ReadinessReviewDriftCode);
        // O build promovido continua sendo EXATAMENTE o do canário — nunca o novo build "drifted".
        Assert.Equal(ValidCommitSha, decision.BuildCommitSha);
    }

    [Fact]
    public async Task IdenticalReplayConvergesToTheSameAuthorizationVersion()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var review = await SeedReadyForCanaryAsync(fixtures.ReviewStore, scope, Now);
        var plan = await SeedAuthorizedCanaryPlanAsync(fixtures.PlanStore, review, scope, Now);
        await MarkCanaryPassedAsync(fixtures.ResultStore, scope, plan, Now);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var first = await useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(first.AuthorizationVersion, second.AuthorizationVersion);
        Assert.Equal(first.AuthorizationFingerprint.Value, second.AuthorizationFingerprint.Value);
        Assert.Single(await fixtures.AuthorizationStore.GetHistoryAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ADifferentTenantNeverSeesAnotherTenantsDecision()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var otherScope = NewScope();
        var review = await SeedReadyForCanaryAsync(fixtures.ReviewStore, scope, Now);
        var plan = await SeedAuthorizedCanaryPlanAsync(fixtures.PlanStore, review, scope, Now);
        await MarkCanaryPassedAsync(fixtures.ResultStore, scope, plan, Now);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        await useCase.ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Null(await fixtures.AuthorizationStore.GetLatestAsync(otherScope, CancellationToken.None));
    }

    [Fact]
    public void GoLiveOutcomeNeverImpliesCompleted()
    {
        Assert.DoesNotContain(Enum.GetNames<GoLiveOutcome>(), name => name.Contains("Completed", StringComparison.OrdinalIgnoreCase));
    }
}
