using System.Data;
using ArchiveBridge.Application.Canary;
using ArchiveBridge.Application.GoLive;
using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Canary;
using ArchiveBridge.Infrastructure.GoLive;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.ProductionReadiness;
using ArchiveBridge.Infrastructure.Recovery;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I8-010 (SQL Server real) — <see cref="AuthorizeGoLiveUseCase"/> e <see cref="SqlGoLiveAuthorizationStore"/>:
/// gate de entrada estruturalmente inalcançável sem NENHUM plano de canário, canário não-passado bloqueia mas
/// é persistido para auditoria, drift do Production Readiness Review vigente contra o vinculado pelo canário
/// bloqueia, revalidação operacional FRESCA via as MESMAS stores canônicas do Passo 1 (nunca a evidência
/// cacheada no review original), RBAC server-side, anti-IDOR cross-tenant, convergência idempotente sob
/// concorrência, e tamper-evidence sobre as tabelas append-only. NUNCA inicia efeito real em
/// Purview/EXO/Graph/EV/AzCopy/host/tenant M365, NUNCA marca migração/projeto/wave concluído (STOP-THE-LINE).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class GoLiveAuthorizationIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly IAuthenticatedActorAccessor ApproverActor =
        new FakeAuthenticatedActorAccessor("approver-1@contoso.com", PortalRoles.Approver);

    private static readonly AzCopyHomologationCatalog HomologatedCatalog =
        new([new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('d', 64)))]);

    private SqlProductionReadinessReviewStore Readiness() => new(fixture.Factory);

    private SqlReadinessControlAttestationStore Attestations() => new(fixture.Factory);

    private SqlCanaryPlanStore Plans() => new(fixture.Factory);

    private SqlCanaryScenarioResultStore Results() => new(fixture.Factory);

    private SqlRecoveryReadinessStore Recovery() => new(fixture.Factory);

    private SqlMailboxPrecheckStore MailboxPrecheck() => new(fixture.Factory);

    private SqlMappingValidationStore MappingValidation() => new(fixture.Factory);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private SqlGoLiveAuthorizationStore Authorizations() => new(fixture.Factory);

    private AuthorizeGoLiveUseCase AuthorizeUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(
            Plans(), Results(), Readiness(), Recovery(), MailboxPrecheck(), MappingValidation(), UploadAttempts(),
            HomologatedCatalog, Attestations(), Authorizations(), Clock, actor ?? ApproverActor);

    private SubmitReadinessControlAttestationUseCase AttestUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Attestations(), Clock, actor ?? ApproverActor);

    private sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    private async Task<ProductionReadinessReviewSnapshot> SeedReadyForCanaryAsync(TenantScope scope, string commitSha = ValidCommitSha)
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
            scope, commitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved, "svc-readiness", "Administrator",
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None);
    }

    private async Task<CanaryPlan> SeedAuthorizedCanaryPlanAsync(TenantScope scope, ProductionReadinessReviewSnapshot review) =>
        await Plans().AuthorizeAsync(
            scope, review.ReviewVersion, review.ReviewFingerprint, ProductionReadinessOutcome.ReadyForCanary, review.BuildCommitSha,
            review.BuildArtifactDigest, review.PolicyVersionFingerprint, review.CapabilityMatrixFingerprint, "approver-1", "Approver",
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None);

    private async Task MarkCanaryPassedAsync(TenantScope scope, CanaryPlan plan)
    {
        foreach (var scenario in CanaryScenarioCatalog.AllScenarios)
        {
            await Results().RecordResultAsync(
                scope, plan.PlanVersion, scenario.Id, CanaryScenarioStatus.Pass,
                CanaryEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{scenario.Id.Value}"), reasonCode: string.Empty,
                Clock.UtcNow, "operator-1", "Operator", CorrelationId.New(), Clock.UtcNow, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithNoCanaryPlanTheEntryGateBlocksWithoutCreatingADecision()
    {
        var scope = SqlServerFixture.NewScope();

        await Assert.ThrowsAsync<GoLiveEntryGateBlockedException>(
            () => AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await Authorizations().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ACanaryPlanThatNeverPassedProducesABlockedDecisionPersistedForAudit()
    {
        var scope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(scope);
        await SeedAuthorizedCanaryPlanAsync(scope, review);

        var decision = await AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(GoLiveOutcome.Blocked, decision.Outcome);
        Assert.Contains(decision.Blockers, blocker => blocker.Code == GoLiveBlocker.CanaryNotPassedCode);
        Assert.NotNull(await Authorizations().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task AViewerCannotAuthorizeGoLiveThroughTheRealStores()
    {
        var scope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(scope);
        var plan = await SeedAuthorizedCanaryPlanAsync(scope, review);
        await MarkCanaryPassedAsync(scope, plan);
        var viewerActor = new FakeAuthenticatedActorAccessor("viewer-1@contoso.com", PortalRoles.Viewer);

        await Assert.ThrowsAsync<GoLiveAuthorizationException>(
            () => AuthorizeUseCase(viewerActor).ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await Authorizations().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task CrossTenantReadNeverReturnsAnotherTenantsDecision()
    {
        var ownerScope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(ownerScope);
        var plan = await SeedAuthorizedCanaryPlanAsync(ownerScope, review);
        await MarkCanaryPassedAsync(ownerScope, plan);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(ownerScope, CorrelationId.New()), CancellationToken.None);

        var otherScope = SqlServerFixture.NewScope();
        Assert.Null(await Authorizations().GetLatestAsync(otherScope, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIdenticalAuthorizationsConvergeToASingleVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(scope);
        var plan = await SeedAuthorizedCanaryPlanAsync(scope, review);
        await MarkCanaryPassedAsync(scope, plan);

        var tasks = Enumerable.Range(0, 5).Select(
            _ => AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(1, r.AuthorizationVersion));
        Assert.Single(await Authorizations().GetHistoryAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ReadingADecisionWithATamperedRowThrowsAnIntegrityViolation()
    {
        var scope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(scope);
        var plan = await SeedAuthorizedCanaryPlanAsync(scope, review);
        await MarkCanaryPassedAsync(scope, plan);
        await AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        await TamperAsync(scope,
            "UPDATE dbo.go_live_authorizations SET build_commit_sha = '1111111111111111111111111111111111111111' " +
            "WHERE tenant_id = @tenant AND project_id = @project;");

        await Assert.ThrowsAsync<GoLiveIntegrityViolationException>(() => Authorizations().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheReadinessReviewDriftsAfterTheCanaryGoLiveBlocksAsDrift()
    {
        var scope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(scope);
        var plan = await SeedAuthorizedCanaryPlanAsync(scope, review);
        await MarkCanaryPassedAsync(scope, plan);

        // Um NOVO Production Readiness Review é composto DEPOIS do canário (digest diferente) — o review
        // vigente já não corresponde ao vinculado pelo plano de canário.
        var otherDigest = new Sha256Hash(new string('b', 64));
        var driftedResolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            driftedResolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture-v2:{definition.Id.Value}"),
                reasonCode: string.Empty, Clock.UtcNow);
        }

        await Readiness().RecordReviewAsync(
            scope, ValidCommitSha, otherDigest, SomeFingerprint, SomeFingerprint, driftedResolved, "svc-readiness", "Administrator",
            CorrelationId.New(), Clock.UtcNow.AddHours(1), CancellationToken.None);

        var decision = await AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(GoLiveOutcome.Blocked, decision.Outcome);
        Assert.Contains(decision.Blockers, blocker => blocker.Code == GoLiveBlocker.ReadinessReviewDriftCode);
        Assert.Equal(review.BuildArtifactDigest.Value, decision.BuildArtifactDigest.Value);
    }

    [Fact]
    public async Task EvenWithEveryResolvableOperationalControlSatisfiedRpoAndArchiveLicenseQuotaStillBlockGoLive()
    {
        var scope = SqlServerFixture.NewScope();
        var review = await SeedReadyForCanaryAsync(scope);
        var plan = await SeedAuthorizedCanaryPlanAsync(scope, review);
        await MarkCanaryPassedAsync(scope, plan);

        // OPS.RTO_EXERCISED: exercício real de restore drill com o objetivo ControlPlaneRto.
        var restoreMeasurement = new RecoveryObjectiveMeasurement(Clock.UtcNow, Clock.UtcNow + TimeSpan.FromHours(1));
        await Recovery().RecordExerciseAsync(
            scope, RecoveryExerciseType.RestoreDrill, RecoveryReadinessStatus.Pass, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), restoreMeasurement, SomeFingerprint, failureDomain: string.Empty, notes: string.Empty,
            "svc-recovery", "ServiceAccount", CorrelationId.New(), Clock.UtcNow, CancellationToken.None);

        // Os sete controles Attested Operations/Microsoft365 — atestados via o MESMO use case do Passo 1.
        string[] attestedOperationalControls =
        [
            "OPS.DASHBOARDS_ALERTS", "OPS.ONCALL_ESCALATION", "OPS.DLQ_RETRY_QUARANTINE_RUNBOOKS", "OPS.CAPACITY_FINOPS",
            "OPS.SUPPORT_PACKAGE_AUTOMATION", "M365.MINIMUM_ROLES", "M365.PORTAL_OPERATOR_TRAINED",
        ];
        foreach (var controlIdValue in attestedOperationalControls)
        {
            await AttestUseCase().ExecuteAsync(
                new SubmitReadinessControlAttestationCommand(
                    scope, new ReadinessControlId(controlIdValue), ReadinessControlStatus.Pass, $"fixture-attestation:{controlIdValue}",
                    ReasonCode: string.Empty, CorrelationId.New()),
                CancellationToken.None);
        }

        var decision = await AuthorizeUseCase().ExecuteAsync(new AuthorizeGoLiveCommand(scope, CorrelationId.New()), CancellationToken.None);

        // Mesmo com TODOS os controles resolvíveis satisfeitos, OPS.RPO_EXERCISED (nunca exercitável nesta
        // baseline) e M365.ARCHIVE_LICENSE_QUOTA (nenhuma fonte canônica existe) seguram o outcome em Blocked
        // — prova executável de que GoLiveAuthorized nunca é fabricado por omissão.
        Assert.Equal(GoLiveOutcome.Blocked, decision.Outcome);
        Assert.Contains(decision.Blockers, b => b.Code.Contains("OPS.RPO_EXERCISED", StringComparison.Ordinal));
        Assert.Contains(decision.Blockers, b => b.Code.Contains("M365.ARCHIVE_LICENSE_QUOTA", StringComparison.Ordinal));

        var rtoResult = decision.OperationalControlResults.Single(r => r.ControlId.Value == "OPS.RTO_EXERCISED");
        Assert.Equal(ReadinessControlStatus.Pass, rtoResult.Status);
        Assert.All(attestedOperationalControls, controlIdValue =>
            Assert.Equal(ReadinessControlStatus.Pass, decision.OperationalControlResults.Single(r => r.ControlId.Value == controlIdValue).Status));
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
