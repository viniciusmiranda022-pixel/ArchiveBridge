using System.Data;
using ArchiveBridge.Application.MigrationCompletion;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.MigrationCompletion;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I8-010 (SQL Server real) — <see cref="ComposeMigrationCompletionAssessmentUseCase"/>,
/// <see cref="SubmitMigrationCompletionCriterionAttestationUseCase"/>, <see cref="SqlMigrationCompletionAssessmentStore"/>
/// e <see cref="SqlMigrationCompletionCriterionAttestationStore"/>: nenhum critério fabricado como Pass sem
/// evidência real, bloqueio estrutural contra atestar um critério SystemDerived, RBAC server-side, anti-IDOR
/// cross-tenant, convergência idempotente sob concorrência, e tamper-evidence sobre as tabelas append-only.
/// NUNCA marca migração/projeto/wave <c>Completed</c>, NUNCA executa decommission/exclusão destrutiva, NUNCA
/// escreve em Purview/EXO/Graph/EV real (STOP-THE-LINE).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MigrationCompletionIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    private static readonly IAuthenticatedActorAccessor ApproverActor =
        new FakeAuthenticatedActorAccessor("approver-1@contoso.com", PortalRoles.Approver);

    private SqlReconciliationCertificateStore Reconciliation() => new(fixture.Factory);

    private SqlPurviewServiceResultReportStore ServiceResults() => new(fixture.Factory);

    private SqlMigrationCompletionCriterionAttestationStore Attestations() => new(fixture.Factory);

    private SqlMigrationCompletionAssessmentStore Assessments() => new(fixture.Factory);

    private ComposeMigrationCompletionAssessmentUseCase ComposeUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Reconciliation(), ServiceResults(), Attestations(), Assessments(), Clock, actor ?? ApproverActor);

    private SubmitMigrationCompletionCriterionAttestationUseCase SubmitUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Attestations(), Clock, actor ?? ApproverActor);

    private sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    private static readonly string[] AttestedCriteria =
    [
        "COMPLETION.SCOPE_AND_POLICY_SIGNED",
        "COMPLETION.SOURCE_DISPOSITION_COMPLETE",
        "COMPLETION.PARTS_DISPOSITION_COMPLETE",
        "COMPLETION.HOLDS_RETENTION_REVIEWED",
        "COMPLETION.USERS_INACTIVE_HANDLED",
        "COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM",
        "COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED",
        "COMPLETION.CUSTOMER_FINAL_APPROVAL",
        "COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL",
    ];

    private async Task AttestAllNineCriteriaAsPassAsync(TenantScope scope)
    {
        foreach (var criterionIdValue in AttestedCriteria)
        {
            await SubmitUseCase().ExecuteAsync(
                new SubmitMigrationCompletionCriterionAttestationCommand(
                    scope, new MigrationCompletionCriterionId(criterionIdValue), ReadinessControlStatus.Pass,
                    $"fixture-attestation:{criterionIdValue}", ReasonCode: string.Empty, CorrelationId.New()),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithNoEvidenceAtAllTheAssessmentIsBlockedForEveryCriterion()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);

        var assessment = await ComposeUseCase().ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(MigrationCompletionOutcome.Blocked, assessment.Outcome);
        Assert.Equal(11, assessment.Blockers.Count);
    }

    [Fact]
    public async Task WithAllNineAttestableCriteriaSatisfiedTheAssessmentStillBlocksOnTheTwoSystemDerivedOnes()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        await AttestAllNineCriteriaAsPassAsync(scope);

        var assessment = await ComposeUseCase().ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        // Nenhum reconciliation certificate/service result report real existe para esta onda/plano — os dois
        // critérios SystemDerived permanecem NotMeasured mesmo com os nove Attested todos Pass (prova
        // executável de que nada é fabricado por omissão).
        Assert.Equal(MigrationCompletionOutcome.Blocked, assessment.Outcome);
        Assert.Equal(2, assessment.Blockers.Count);
        Assert.Contains(assessment.Blockers, b => b.CriterionId.Value == "COMPLETION.RECONCILIATION_CLOSED");
        Assert.Contains(assessment.Blockers, b => b.CriterionId.Value == "COMPLETION.PROVIDER_RESULTS_COLLECTED");
        Assert.All(AttestedCriteria, criterionIdValue =>
            Assert.Equal(
                ReadinessControlStatus.Pass,
                assessment.CriterionResults.Single(r => r.CriterionId.Value == criterionIdValue).Status));
    }

    [Fact]
    public async Task AViewerCannotComposeAnAssessmentThroughTheRealStores()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var viewerActor = new FakeAuthenticatedActorAccessor("viewer-1@contoso.com", PortalRoles.Viewer);

        await Assert.ThrowsAsync<MigrationCompletionAuthorizationException>(() => ComposeUseCase(viewerActor).ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await Assessments().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task AttestingASystemDerivedCriterionIsRefusedEvenAgainstTheRealStore()
    {
        var scope = SqlServerFixture.NewScope();

        await Assert.ThrowsAsync<MigrationCompletionAttestationNotAllowedException>(() => SubmitUseCase().ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                scope, new MigrationCompletionCriterionId("COMPLETION.RECONCILIATION_CLOSED"), ReadinessControlStatus.Pass,
                "manual override attempt", ReasonCode: string.Empty, CorrelationId.New()),
            CancellationToken.None));

        Assert.Null(await Attestations().GetLatestAsync(scope, new MigrationCompletionCriterionId("COMPLETION.RECONCILIATION_CLOSED"), CancellationToken.None));
    }

    [Fact]
    public async Task CrossTenantReadNeverReturnsAnotherTenantsAssessmentOrAttestation()
    {
        var ownerScope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(ownerScope.Tenant, ownerScope.Project, wave, 1);
        await ComposeUseCase().ExecuteAsync(new ComposeMigrationCompletionAssessmentCommand(ownerScope, wave, jobName, CorrelationId.New()), CancellationToken.None);
        await SubmitUseCase().ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                ownerScope, new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), ReadinessControlStatus.Pass,
                "customer-signoff:v1", ReasonCode: string.Empty, CorrelationId.New()),
            CancellationToken.None);

        var otherScope = SqlServerFixture.NewScope();
        Assert.Null(await Assessments().GetLatestAsync(otherScope, CancellationToken.None));
        Assert.Null(await Attestations().GetLatestAsync(otherScope, new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL"), CancellationToken.None));
    }

    [Fact]
    public async Task IdenticalReplayConvergesToTheSameAssessmentVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);

        var first = await ComposeUseCase().ExecuteAsync(new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);
        var second = await ComposeUseCase().ExecuteAsync(new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(first.AssessmentVersion, second.AssessmentVersion);
        Assert.Single(await Assessments().GetHistoryAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIdenticalComposesConvergeToASingleVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);

        var tasks = Enumerable.Range(0, 5).Select(
            _ => ComposeUseCase().ExecuteAsync(new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(1, r.AssessmentVersion));
        Assert.Single(await Assessments().GetHistoryAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentIdenticalAttestationSubmissionsConvergeToASingleVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var command = new SubmitMigrationCompletionCriterionAttestationCommand(
            scope, new MigrationCompletionCriterionId("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL"), ReadinessControlStatus.Pass,
            "credential-registry-review:v1", ReasonCode: string.Empty, CorrelationId.New());

        var tasks = Enumerable.Range(0, 5).Select(_ => SubmitUseCase().ExecuteAsync(command, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(1, r.AttestationVersion));
    }

    [Fact]
    public async Task ReadingAnAssessmentWithATamperedRowThrowsAnIntegrityViolation()
    {
        var scope = SqlServerFixture.NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        await ComposeUseCase().ExecuteAsync(new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        await TamperAsync(scope, "UPDATE dbo.migration_completion_assessments SET outcome = 1 WHERE tenant_id = @tenant AND project_id = @project;");

        await Assert.ThrowsAsync<MigrationCompletionIntegrityViolationException>(() => Assessments().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ReadingAnAttestationWithATamperedRowThrowsAnIntegrityViolation()
    {
        var scope = SqlServerFixture.NewScope();
        var criterionId = new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL");
        await SubmitUseCase().ExecuteAsync(
            new SubmitMigrationCompletionCriterionAttestationCommand(
                scope, criterionId, ReadinessControlStatus.Pass, "customer-signoff:v1", ReasonCode: string.Empty, CorrelationId.New()),
            CancellationToken.None);

        await TamperAsync(scope,
            "UPDATE dbo.migration_completion_criterion_attestations SET status = 3 " +
            "WHERE tenant_id = @tenant AND project_id = @project AND criterion_id = 'COMPLETION.CUSTOMER_FINAL_APPROVAL';");

        await Assert.ThrowsAsync<MigrationCompletionIntegrityViolationException>(
            () => Attestations().GetLatestAsync(scope, criterionId, CancellationToken.None));
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
