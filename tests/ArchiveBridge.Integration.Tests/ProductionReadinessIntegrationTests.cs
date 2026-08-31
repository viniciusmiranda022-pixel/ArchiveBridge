using System.Data;
using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.ProductionReadiness;
using ArchiveBridge.Infrastructure.Recovery;
using ArchiveBridge.Infrastructure.Security;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I8-001 (SQL Server real) — <see cref="ComposeProductionReadinessReviewUseCase"/>,
/// <see cref="SubmitReadinessControlAttestationUseCase"/>, <see cref="SqlProductionReadinessReviewStore"/> e
/// <see cref="SqlReadinessControlAttestationStore"/>: pen-test/RTO/RPO estruturalmente nunca Pass mesmo com
/// TODOS os demais controles atestados/verificados, RBAC server-side, anti-IDOR cross-tenant, convergência
/// idempotente sob concorrência, supersession por mudança real de evidência, e tamper-evidence sobre a
/// tabela append-only. NUNCA inicia canário real, NUNCA marca projeto concluído, NUNCA escreve em
/// Purview/EXO/Graph/EV/AzCopy/host real (STOP-THE-LINE).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ProductionReadinessIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly IAuthenticatedActorAccessor ApproverActor =
        new FakeAuthenticatedActorAccessor("approver-1@contoso.com", PortalRoles.Approver);

    private SqlReadinessControlAttestationStore Attestations() => new(fixture.Factory);

    private SqlProductionReadinessReviewStore Reviews() => new(fixture.Factory);

    private SqlPenTestReadinessStore PenTest() => new(fixture.Factory);

    private SqlWorkerHardeningBaselineStore Hardening() => new(fixture.Factory);

    private SqlWdacPolicyEvidenceStore Wdac() => new(fixture.Factory);

    private SqlIncidentResponseDrillStore IncidentResponse() => new(fixture.Factory);

    private SqlBuildProvenanceStore BuildProvenance() => new(fixture.Factory);

    private SqlRecoveryReadinessStore Recovery() => new(fixture.Factory);

    private SqlCapabilityEvidenceStore Capability() => new(fixture.Factory);

    private SqlMailboxPrecheckStore MailboxPrecheck() => new(fixture.Factory);

    private SqlMappingValidationStore MappingValidation() => new(fixture.Factory);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private static readonly AzCopyBinaryIdentity HomologatedBinary = new("10.25.0", new Sha256Hash(new string('d', 64)));
    private static readonly AzCopyHomologationCatalog HomologatedCatalog = new([HomologatedBinary]);

    private ComposeProductionReadinessReviewUseCase ComposeUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(
            PenTest(), Hardening(), Wdac(), IncidentResponse(), BuildProvenance(), Recovery(), Capability(), MailboxPrecheck(),
            MappingValidation(), UploadAttempts(), HomologatedCatalog, Attestations(), Reviews(), Clock, actor ?? ApproverActor);

    private SubmitReadinessControlAttestationUseCase SubmitUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Attestations(), Clock, actor ?? ApproverActor);

    private static ComposeProductionReadinessReviewCommand ComposeCommand(TenantScope scope) =>
        new(scope, ValidCommitSha, SomeFingerprint, "ArchiveBridge.ControlPlane", CorrelationId.New());

    private sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    [Fact]
    public async Task SubmittingAndReadingBackAnAttestationRoundTrips()
    {
        var scope = SqlServerFixture.NewScope();
        var attestation = await SubmitUseCase().ExecuteAsync(
            new SubmitReadinessControlAttestationCommand(
                scope, new ReadinessControlId("ARCH.ADR_APPROVED"), ReadinessControlStatus.Pass,
                "ADR-0031 approved in architecture review 2026-08-15", ReasonCode: string.Empty, CorrelationId.New()),
            CancellationToken.None);

        var latest = await Attestations().GetLatestAsync(scope, new ReadinessControlId("ARCH.ADR_APPROVED"), CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(attestation.RecordHash, latest!.RecordHash);
        Assert.Equal(ReadinessControlStatus.Pass, latest.Status);
    }

    [Fact]
    public async Task ComposingWithNoEvidenceAnywhereProducesNotReady()
    {
        var scope = SqlServerFixture.NewScope();

        var snapshot = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        Assert.Equal(ProductionReadinessOutcome.NotReady, snapshot.Outcome);
        Assert.Equal(32, snapshot.ControlResults.Count);
    }

    [Fact]
    public async Task ReadyForCanaryIsStructurallyUnreachableEvenWhenEveryOtherControlPasses()
    {
        var scope = SqlServerFixture.NewScope();
        var now = Clock.UtcNow;

        // Atesta TODOS os 18 controles Attested como Pass (AB-I8-002 reclassificou 4 controles para
        // SystemDerived: ARCH.CAPABILITY_MATRIX_CURRENT/M365.TENANT_PRECHECK/M365.MAPPING_VALIDATOR/
        // M365.AZCOPY_VERSION_HOMOLOGATED — nenhum destes é mais atestável, resolvidos abaixo a partir de
        // evidência canônica real).
        foreach (var definition in ReadinessControlCatalog.AllControls.Where(d => d.EvidenceSource == ReadinessControlEvidenceSource.Attested))
        {
            await SubmitUseCase().ExecuteAsync(
                new SubmitReadinessControlAttestationCommand(
                    scope, definition.Id, ReadinessControlStatus.Pass, $"evidence for {definition.Id.Value}",
                    ReasonCode: string.Empty, CorrelationId.New()),
                CancellationToken.None);
        }

        // Faz todos os controles SystemDerived (exceto pen-test/RPO, estruturalmente nunca Pass) passarem.
        var measurement = new WorkerHardeningMeasurement(now, "integration-test measurement");
        foreach (var control in WorkerHardeningBaselineCatalog.AllControls)
        {
            if (WorkerHardeningBaselineCatalog.Applicability(control) != WorkerHardeningApplicability.Required)
            {
                continue;
            }

            await Hardening().RecordControlAsync(
                scope, control, WorkerHardeningStatus.Pass, measurement, SomeFingerprint, blockedReason: string.Empty,
                notes: string.Empty, "svc-hardening", "ServiceAccount", CorrelationId.New(), now, CancellationToken.None);
        }

        await Wdac().RecordPolicyAsync(
            scope, [WdacAllowlistEntry.Create(publisher: null, sha256: SomeFingerprint, pathRule: null)],
            "svc-hardening", "ServiceAccount", CorrelationId.New(), now, CancellationToken.None);

        foreach (var drillType in Enum.GetValues<IncidentResponseDrillType>())
        {
            await IncidentResponse().RecordDrillAsync(
                scope, drillType, IncidentResponseDrillOutcome.Contained, now, now + TimeSpan.FromMinutes(5), SomeFingerprint,
                disposition: "contained as expected", "svc-security", "ServiceAccount", CorrelationId.New(), now, CancellationToken.None);
        }

        await BuildProvenance().ApproveAsync(
            scope, "ArchiveBridge.ControlPlane", ValidCommitSha, "ci-runner", now, SomeFingerprint, "svc-supply-chain",
            "ServiceAccount", CorrelationId.New(), now, CancellationToken.None);

        var restoreMeasurement = new RecoveryObjectiveMeasurement(now, now + TimeSpan.FromHours(1));
        await Recovery().RecordExerciseAsync(
            scope, RecoveryExerciseType.RestoreDrill, RecoveryReadinessStatus.Pass, RecoveryObjective.ControlPlaneRto,
            TimeSpan.FromHours(4), restoreMeasurement, SomeFingerprint, failureDomain: string.Empty, notes: string.Empty,
            "svc-recovery", "ServiceAccount", CorrelationId.New(), now, CancellationToken.None);
        await Recovery().RecordExerciseAsync(
            scope, RecoveryExerciseType.ArtifactEvidenceRecovery, RecoveryReadinessStatus.Pass, RecoveryObjective.None,
            objectiveThreshold: null, restoreMeasurement, SomeFingerprint, failureDomain: string.Empty, notes: string.Empty,
            "svc-recovery", "ServiceAccount", CorrelationId.New(), now, CancellationToken.None);

        // Pen-test: o máximo possível é Blocked — PenTestReadinessStatus não possui Pass.
        await PenTest().RecordBundleAsync(
            scope, PenTestReadinessStatus.Blocked, "scope", "attack-surface", "trust-boundaries", "fixtures",
            "known-blocked", SomeFingerprint, "no independent tester contracted yet", "svc-security", "ServiceAccount",
            CorrelationId.New(), now, CancellationToken.None);

        // ARCH.CAPABILITY_MATRIX_CURRENT (AB-I8-002): evidência real GA/fresca para a rota conhecida.
        var capabilityEvidence = CapabilityEvidence.Record(
            CapabilityEvidenceId.New(), scope.Tenant, scope.Project, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport,
            version: 1, CapabilityStatus.GeneralAvailability, "ADR-0006", null, null, now, CorrelationId.New(), now);
        await Capability().AppendAsync(capabilityEvidence, CancellationToken.None);

        // M365.TENANT_PRECHECK (AB-I8-002): precheck de mailbox real com archive Active.
        var precheckSnapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project,
            new ArchiveRef("readiness-review@contoso.example", TargetArchiveId.FromMailbox("readiness-review@contoso.example")),
            version: 1, exchangeGuid: Guid.NewGuid(), archiveGuid: Guid.NewGuid(), MailboxArchiveStatus.Active, "UserMailbox",
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: 10, archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000, now, CorrelationId.New(), now);
        await MailboxPrecheck().AppendAsync(precheckSnapshot, CancellationToken.None);

        var snapshot = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        // Mesmo com TODOS os demais controles resolvíveis em Pass, pen-test (e RPO, nunca exercitado) seguram
        // o outcome em NotReady — prova executável das acceptance criteria 2/3/4. M365.MAPPING_VALIDATOR e
        // M365.AZCOPY_VERSION_HOMOLOGATED (AB-I8-002) não são seedados aqui (cobertos em testes dedicados
        // abaixo, que exigem uma onda/pedido de upload reais) — permanecem NotMeasured, também bloqueando.
        Assert.Equal(ProductionReadinessOutcome.NotReady, snapshot.Outcome);
        Assert.Contains(snapshot.Blockers, b => b.ControlId.Value == "SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");
        Assert.Contains(snapshot.Blockers, b => b.ControlId.Value == "OPS.RPO_EXERCISED");
        Assert.DoesNotContain(snapshot.Blockers, b => b.ControlId.Value == "ARCH.CAPABILITY_MATRIX_CURRENT");
        Assert.DoesNotContain(snapshot.Blockers, b => b.ControlId.Value == "M365.TENANT_PRECHECK");
    }

    [Fact]
    public async Task MappingValidatorResolvesToPassFromARealValidatedMappingAttempt()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var waveStore = Slice2Support.WaveStore(fixture);
        await waveStore.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await waveStore.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);

        var attempt = new MappingValidationAttempt(
            Guid.NewGuid(), scope, wave.Id, wave.Version.Value, wave.ConfigurationHash, wave.SelectionHash,
            MappingSchema.Version, MappingPolicy.Default.Version, new ContentCodePage(1252), SomeFingerprint,
            SizeBytes: 128, RowCount: 1, MappingValidationAttemptOutcome.Valid, IssueCount: 0, IssuesTruncated: false,
            "mapping.csv", Guid.NewGuid(), "operator", CorrelationId.New(), Guid.NewGuid(), Clock.UtcNow, []);
        await MappingValidation().PersistAsync(attempt, CancellationToken.None);

        var snapshot = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        var result = snapshot.ControlResults.Single(r => r.ControlId.Value == "M365.MAPPING_VALIDATOR");
        Assert.Equal(ReadinessControlStatus.Pass, result.Status);
    }

    [Fact]
    public async Task CrossTenantReadNeverReturnsAnotherTenantsSnapshot()
    {
        var ownerScope = SqlServerFixture.NewScope();
        await ComposeUseCase().ExecuteAsync(ComposeCommand(ownerScope), CancellationToken.None);

        var otherScope = SqlServerFixture.NewScope();
        var crossTenantRead = await Reviews().GetLatestAsync(otherScope, CancellationToken.None);

        Assert.Null(crossTenantRead);
    }

    [Fact]
    public async Task IdenticalReplayConvergesToTheSameVersionWithoutDuplicatingRows()
    {
        var scope = SqlServerFixture.NewScope();

        var first = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);
        var second = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        Assert.Equal(first.ReviewVersion, second.ReviewVersion);
        var history = await Reviews().GetHistoryAsync(scope, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task ConcurrentIdenticalComposesConvergeToASingleVersion()
    {
        var scope = SqlServerFixture.NewScope();

        var tasks = Enumerable.Range(0, 5).Select(_ => ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(1, r.ReviewVersion));
        var history = await Reviews().GetHistoryAsync(scope, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task NewEvidenceSupersedesThePreviousSnapshotWithANewVersion()
    {
        var scope = SqlServerFixture.NewScope();
        var before = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        await PenTest().RecordBundleAsync(
            scope, PenTestReadinessStatus.Blocked, "scope", "attack-surface", "trust-boundaries", "fixtures",
            "known-blocked", SomeFingerprint, "no independent tester contracted yet", "svc-security", "ServiceAccount",
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None);

        var after = await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        Assert.True(after.ReviewVersion > before.ReviewVersion);
        var history = await Reviews().GetHistoryAsync(scope, CancellationToken.None);
        Assert.Equal(2, history.Count);

        var latest = await Reviews().GetLatestAsync(scope, CancellationToken.None);
        Assert.Equal(after.ReviewVersion, latest!.ReviewVersion);
    }

    [Fact]
    public async Task ReadingASnapshotWithATamperedControlRowThrowsAnIntegrityViolation()
    {
        var scope = SqlServerFixture.NewScope();
        await ComposeUseCase().ExecuteAsync(ComposeCommand(scope), CancellationToken.None);

        await using (var connection = new SqlConnection(fixture.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using (var context = new SqlCommand(
                "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
            {
                context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                await context.ExecuteNonQueryAsync();
            }

            await using var tamper = new SqlCommand(
                "UPDATE dbo.production_readiness_review_control_results SET status = 4, evidence_kind = 1 " +
                "WHERE tenant_id = @tenant AND project_id = @project AND control_id = 'ARCH.ADR_APPROVED';",
                connection);
            tamper.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            tamper.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await tamper.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<ProductionReadinessIntegrityViolationException>(
            () => Reviews().GetLatestAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task AViewerCannotComposeAReviewThroughTheRealStores()
    {
        var scope = SqlServerFixture.NewScope();
        var viewerActor = new FakeAuthenticatedActorAccessor("viewer-1@contoso.com", PortalRoles.Viewer);

        await Assert.ThrowsAsync<ProductionReadinessAuthorizationException>(
            () => ComposeUseCase(viewerActor).ExecuteAsync(ComposeCommand(scope), CancellationToken.None));
    }

    [Fact]
    public async Task SubmittingAnAttestationForASystemDerivedControlIsRejectedEvenAgainstTheRealStore()
    {
        var scope = SqlServerFixture.NewScope();

        await Assert.ThrowsAsync<ProductionReadinessAttestationNotAllowedException>(() => SubmitUseCase().ExecuteAsync(
            new SubmitReadinessControlAttestationCommand(
                scope, new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH"), ReadinessControlStatus.Pass,
                "I promise it passed", ReasonCode: string.Empty, CorrelationId.New()),
            CancellationToken.None));

        var persisted = await Attestations().GetLatestAsync(scope, new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH"), CancellationToken.None);
        Assert.Null(persisted);
    }
}
