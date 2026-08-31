using System.Data;
using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Infrastructure.Canary;
using ArchiveBridge.Infrastructure.ProductionReadiness;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I8-004 (SQL Server real) — <see cref="AuthorizeCanaryPlanUseCase"/>,
/// <see cref="SubmitCanaryScenarioEvidenceUseCase"/>, <see cref="ApproveCanaryFirstWaveUseCase"/>,
/// <see cref="GetCanaryPlanReportUseCase"/>, <see cref="SqlCanaryPlanStore"/> e
/// <see cref="SqlCanaryScenarioResultStore"/>: gate de entrada estruturalmente inalcançável sem
/// ReadyForCanary, RBAC server-side, anti-IDOR cross-tenant, convergência idempotente sob concorrência,
/// supersession por drift, tamper-evidence sobre as tabelas append-only, e bloqueio estrutural da aprovação
/// da primeira onda enquanto qualquer outro cenário não está Pass. NUNCA inicia canário real, NUNCA marca
/// projeto/wave concluído, NUNCA escreve em Purview/EXO/Graph/EV/AzCopy/host real (STOP-THE-LINE).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class CanaryAcceptanceIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly IAuthenticatedActorAccessor ApproverActor =
        new FakeAuthenticatedActorAccessor("approver-1@contoso.com", PortalRoles.Approver);

    private static readonly IAuthenticatedActorAccessor OperatorActor =
        new FakeAuthenticatedActorAccessor("operator-1@contoso.com", PortalRoles.Operator);

    private SqlProductionReadinessReviewStore Readiness() => new(fixture.Factory);

    private SqlCanaryPlanStore Plans() => new(fixture.Factory);

    private SqlCanaryScenarioResultStore Results() => new(fixture.Factory);

    private AuthorizeCanaryPlanUseCase AuthorizeUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Readiness(), Plans(), Clock, actor ?? ApproverActor);

    private SubmitCanaryScenarioEvidenceUseCase SubmitUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Results(), Clock, actor ?? OperatorActor);

    private ApproveCanaryFirstWaveUseCase ApproveUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Results(), Clock, actor ?? ApproverActor);

    private GetCanaryPlanReportUseCase ReportUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Plans(), Results(), Readiness(), Clock, actor ?? OperatorActor);

    private sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    /// <summary>Persiste, via a store REAL, um Production Readiness Review ReadyForCanary (todos os 32 controles Pass) — não reexercita a resolução de evidência de I8-001 (já coberta por seus próprios testes), apenas materializa o estado de entrada necessário para exercitar o canário.</summary>
    private async Task<ProductionReadinessReviewSnapshot> SeedReadyForCanaryAsync(TenantScope scope)
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Clock.UtcNow);
        }

        return await Readiness().RecordReviewAsync(
            scope, ValidCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved, "svc-readiness", "Administrator",
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task AuthorizingWithoutAnyReadinessReviewBlocksWithoutCreatingAPlan()
    {
        var scope = SqlServerFixture.NewScope();

        await Assert.ThrowsAsync<CanaryEntryGateBlockedException>(
            () => AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await Plans().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorizingSucceedsOnceReadinessIsReadyForCanary()
    {
        var scope = SqlServerFixture.NewScope();
        var readiness = await SeedReadyForCanaryAsync(scope);

        var plan = await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(1, plan.PlanVersion);
        Assert.Equal(readiness.ReviewVersion, plan.ReadinessReviewVersion);
        Assert.Equal(ValidCommitSha, plan.BuildCommitSha);
    }

    [Fact]
    public async Task ConcurrentIdenticalAuthorizationsConvergeToASingleVersion()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);

        var tasks = Enumerable.Range(0, 5).Select(_ => AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(1, r.PlanVersion));
        var history = await Plans().GetHistoryAsync(scope, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task CrossTenantReadNeverReturnsAnotherTenantsPlan()
    {
        var ownerScope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(ownerScope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(ownerScope, CorrelationId.New()), CancellationToken.None);

        var otherScope = SqlServerFixture.NewScope();
        var crossTenantRead = await Plans().GetLatestAsync(otherScope, CancellationToken.None);

        Assert.Null(crossTenantRead);
    }

    [Fact]
    public async Task AViewerCannotAuthorizeAPlanThroughTheRealStores()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        var viewerActor = new FakeAuthenticatedActorAccessor("viewer-1@contoso.com", PortalRoles.Viewer);

        await Assert.ThrowsAsync<CanaryAuthorizationException>(
            () => AuthorizeUseCase(viewerActor).ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await Plans().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ReadingAPlanWithATamperedRowThrowsAnIntegrityViolation()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        await TamperAsync(scope, "UPDATE dbo.canary_plans SET build_commit_sha = '1111111111111111111111111111111111111111' WHERE tenant_id = @tenant AND project_id = @project;");

        await Assert.ThrowsAsync<CanaryIntegrityViolationException>(() => Plans().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task SubmittingEvidenceForASystemDerivedScenarioIsRejectedEvenAgainstTheRealStore()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        await Assert.ThrowsAsync<CanaryScenarioNotAttestableException>(() => SubmitUseCase().ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(scope, 1, new CanaryScenarioId("CANARY.CRASH_RECOVERY"), CanaryScenarioStatus.Pass,
                "it definitely recovered", string.Empty, Clock.UtcNow, CorrelationId.New()),
            CancellationToken.None));

        Assert.Null(await Results().GetLatestAsync(scope, 1, new CanaryScenarioId("CANARY.CRASH_RECOVERY"), CancellationToken.None));
    }

    [Fact]
    public async Task SubmittingAgainstASupersededPlanVersionIsRejected()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        // Nova revisão de readiness supera a primeira -> nova versão do plano de canário. O digest do
        // artifact revisado muda de verdade (não apenas o locator) para que ReviewFingerprint realmente
        // divirja — ComputeReviewFingerprint nunca cobre o locator, só o digest do fingerprint em si.
        var otherArtifactDigest = new Sha256Hash(new string('b', 64));
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture-v2:{definition.Id.Value}"),
                reasonCode: string.Empty, Clock.UtcNow);
        }

        await Readiness().RecordReviewAsync(
            scope, ValidCommitSha, otherArtifactDigest, SomeFingerprint, SomeFingerprint, resolved, "svc-readiness", "Administrator",
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        await Assert.ThrowsAsync<CanaryPlanSupersededException>(() => SubmitUseCase().ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(scope, 1, new CanaryScenarioId("CANARY.CORPUS_ITEM_TYPE_DIVERSITY"), CanaryScenarioStatus.Pass,
                "20 item types observed", string.Empty, Clock.UtcNow, CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIdenticalScenarioSubmissionsConvergeToASingleResultVersion()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        var scenarioId = new CanaryScenarioId("CANARY.CORPUS_ITEM_TYPE_DIVERSITY");
        // Congela um único instante ANTES de disparar as 5 submissões concorrentes — content_fingerprint
        // cobre ObservedAtUtc (evidência de "quando foi observado", ao contrário de ReviewFingerprint em
        // ProductionReadiness, que nunca cobre timestamp); usar Clock.UtcNow (relógio real) dentro do lambda
        // do Select produziria valores realmente distintos entre as 5 chamadas e um falso-positivo de
        // "não convergiu", não uma falha de concorrência real.
        var observedAt = Clock.UtcNow;
        var tasks = Enumerable.Range(0, 5).Select(_ => SubmitUseCase().ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(scope, 1, scenarioId, CanaryScenarioStatus.Pass, "20 item types observed", string.Empty, observedAt, CorrelationId.New()),
            CancellationToken.None));
        await Task.WhenAll(tasks);

        var history = await Results().GetHistoryAsync(scope, 1, scenarioId, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task ReadingAScenarioResultWithATamperedRowThrowsAnIntegrityViolation()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);
        var scenarioId = new CanaryScenarioId("CANARY.CORPUS_ITEM_TYPE_DIVERSITY");
        await SubmitUseCase().ExecuteAsync(
            new SubmitCanaryScenarioEvidenceCommand(scope, 1, scenarioId, CanaryScenarioStatus.Pass, "20 item types observed", string.Empty, Clock.UtcNow, CorrelationId.New()),
            CancellationToken.None);

        await TamperAsync(scope, "UPDATE dbo.canary_scenario_results SET status = 4 WHERE tenant_id = @tenant AND project_id = @project AND scenario_id = 'CANARY.CORPUS_ITEM_TYPE_DIVERSITY';");

        await Assert.ThrowsAsync<CanaryIntegrityViolationException>(() => Results().GetLatestAsync(scope, 1, scenarioId, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalIsBlockedUntilAllOtherScenariosArePass()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        await Assert.ThrowsAsync<CanaryFirstWaveApprovalBlockedException>(
            () => ApproveUseCase().ExecuteAsync(new ApproveCanaryFirstWaveCommand(scope, 1, Notes: null, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task FullHappyPathReachesCanaryPassedAndIsPromotable()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedReadyForCanaryAsync(scope);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);

        var operatorAttested = new[]
        {
            "CANARY.CORPUS_ITEM_TYPE_DIVERSITY",
            "CANARY.PST_SIZE_BOUNDARY_COVERAGE",
            "CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT",
            "CANARY.DIFFERENT_TARGET_ROOT_BLOCKS",
            "CANARY.KNOWN_CORRUPTION_QUARANTINE",
        };
        foreach (var scenarioIdValue in operatorAttested)
        {
            await SubmitUseCase().ExecuteAsync(
                new SubmitCanaryScenarioEvidenceCommand(scope, 1, new CanaryScenarioId(scenarioIdValue), CanaryScenarioStatus.Pass,
                    $"observed evidence for {scenarioIdValue}", string.Empty, Clock.UtcNow, CorrelationId.New()),
                CancellationToken.None);
        }

        // Os quatro SystemDerived não têm store canônico seedado neste teste (não são o foco de
        // ResolveCanarySystemEvidenceUseCase, já coberto em Application.Tests) — persistidos aqui diretamente
        // via a store REAL para completar o happy path e provar a aprovação/promoção fim a fim.
        foreach (var scenarioIdValue in new[] { "CANARY.TENANT_MAILBOX_CONTROLLED", "CANARY.CRASH_RECOVERY", "CANARY.RECONCILIATION_EVIDENCE_PACKAGE", "CANARY.RESTORE_ROLLBACK_OPERATIONAL" })
        {
            await Results().RecordResultAsync(
                scope, 1, new CanaryScenarioId(scenarioIdValue), CanaryScenarioStatus.Pass, ArchiveBridge.Domain.Canary.CanaryEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{scenarioIdValue}"),
                reasonCode: string.Empty, Clock.UtcNow, "svc-canary", "ServiceAccount", CorrelationId.New(), Clock.UtcNow, CancellationToken.None);
        }

        await ApproveUseCase().ExecuteAsync(new ApproveCanaryFirstWaveCommand(scope, 1, "low-criticality first wave approved", CorrelationId.New()), CancellationToken.None);

        var report = await ReportUseCase().ExecuteAsync(new GetCanaryPlanReportQuery(scope), CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(CanaryOutcome.CanaryPassed, report!.Outcome);
        Assert.True(report.IsPromotable);
        Assert.False(report.ReadinessHasDrifted);
        Assert.Empty(report.BlockerSummaries);
    }

    private async Task TamperAsync(TenantScope scope, string updateSql)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var tamper = new SqlCommand(updateSql, connection);
        tamper.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        tamper.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await tamper.ExecuteNonQueryAsync();
    }
}
