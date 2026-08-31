using ArchiveBridge.Application.MigrationCompletion;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using Xunit;
using Canary = ArchiveBridge.Application.Tests.Canary;

namespace ArchiveBridge.Application.Tests.MigrationCompletion;

/// <summary>
/// AB-I8-010/AB-I8-011/AB-I8-012 — <see cref="ComposeMigrationCompletionAssessmentUseCase"/>: RBAC
/// server-side, nenhum critério fabricado como Pass sem evidência real, cada critério ausente individualmente
/// bloqueia <c>Eligible</c>, reconciliation Inconclusive/Fail/evidência incompleta bloqueia mesmo com todos os
/// demais Pass, replay idêntico converge, e os cinco critérios EvidenceDerived (AB-I8-011/AB-I8-012)
/// permanecem bloqueantes mesmo quando TODOS os demais critérios estão satisfeitos — nunca satisfeitos por
/// atestação.
/// </summary>
public sealed class ComposeMigrationCompletionAssessmentUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private sealed class Fixtures
    {
        public Canary.InMemoryReconciliationCertificateStore ReconciliationStore { get; } = new();
        public InMemoryPurviewServiceResultReportStore ServiceResultStore { get; } = new();
        public InMemoryMigrationCompletionCriterionAttestationStore AttestationStore { get; } = new();
        public InMemoryMigrationCompletionAssessmentStore AssessmentStore { get; } = new();

        public ComposeMigrationCompletionAssessmentUseCase BuildUseCase(Contracts.Abstractions.IAuthenticatedActorAccessor actorAccessor) =>
            new(ReconciliationStore, ServiceResultStore, AttestationStore, AssessmentStore, new Canary.FixedClock(Now), actorAccessor);
    }

    // Os quatro critérios HumanApproval — os únicos que podem ser atestados manualmente (AB-I8-011/AB-I8-012).
    private static readonly string[] AttestedCriteria =
    [
        "COMPLETION.SCOPE_AND_POLICY_SIGNED",
        "COMPLETION.HOLDS_RETENTION_REVIEWED",
        "COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED",
        "COMPLETION.CUSTOMER_FINAL_APPROVAL",
    ];

    // Os cinco critérios EvidenceDerived (AB-I8-011/AB-I8-012) — tecnicamente objetivos, sem store canônico
    // suficiente neste repositório; permanecem SEMPRE NotMeasured com um reason code específico e estável,
    // nunca satisfeitos por atestação.
    private static readonly (string CriterionId, string ReasonCode)[] EvidenceDerivedCriteria =
    [
        ("COMPLETION.SOURCE_DISPOSITION_COMPLETE", "NO_CANONICAL_SOURCE_DISPOSITION_STORE"),
        ("COMPLETION.PARTS_DISPOSITION_COMPLETE", "NO_CANONICAL_PARTS_DISPOSITION_STORE"),
        ("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM", "NO_CANONICAL_EVIDENCE_PACKAGE_WORM_PUBLICATION_STORE"),
        ("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL", "NO_CANONICAL_TEMPORARY_CREDENTIAL_REGISTRY"),
        ("COMPLETION.USERS_INACTIVE_HANDLED", "NO_CANONICAL_USER_INACTIVE_DISPOSITION_STORE"),
    ];

    private static void SeedAllAttestedCriteriaAsPass(InMemoryMigrationCompletionCriterionAttestationStore store, TenantScope scope)
    {
        foreach (var criterionIdValue in AttestedCriteria)
        {
            var criterionId = new MigrationCompletionCriterionId(criterionIdValue);
            var attestation = MigrationCompletionCriterionAttestation.Create(
                scope.Tenant, scope.Project, criterionId, 1, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.Attested(SomeFingerprint, $"fixture:{criterionIdValue}"), reasonCode: string.Empty,
                "approver-1", "Approver", CorrelationId.New(), Now);
            store.SeedBypassingUseCase(scope, attestation);
        }
    }

    private static ReconciliationCertificate SeedClosedReconciliation(
        Canary.InMemoryReconciliationCertificateStore store, TenantScope scope, WaveId wave, PurviewImportJobName jobName)
    {
        var certificate = ReconciliationCertificate.Create(
            scope.Tenant, scope.Project, wave, jobName, certificateVersion: 1, assessmentVersion: 1, SomeFingerprint, SomeFingerprint,
            ReconciliationOutcome.Pass, totalItemCount: 10, incompleteItemCount: 0, deviationCount: 0, SomeFingerprint, SomeFingerprint,
            duplicateRiskDetected: false, "approver-1", "Approver", CorrelationId.New(), Now);
        store.Seed(scope, wave, jobName, certificate);
        return certificate;
    }

    private static void SeedProviderResultsCollected(
        InMemoryPurviewServiceResultReportStore store, TenantScope scope, WaveId wave, PurviewImportJobName jobName)
    {
        var evidence = PurviewServiceResultReportEvidence.Create(
            scope.Tenant, scope.Project, wave, jobName, reportVersion: 1, SomeFingerprint, SomeFingerprint, rawSizeBytes: 1024,
            rowCount: 10, declaredTotalRows: 10, "operator-1", Now);
        store.Seed(scope, wave, jobName, evidence);
    }

    [Fact]
    public async Task AnonymousActorIsRejectedBeforeAnyScopedAccess()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var useCase = fixtures.BuildUseCase(new Canary.UnauthenticatedActorAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task AViewerRoleCannotComposeAnAssessment()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Viewer"));

        await Assert.ThrowsAsync<MigrationCompletionAuthorizationException>(() => useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task WithNoEvidenceAtAllTheOutcomeIsBlockedForEveryCriterion()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));

        var assessment = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(MigrationCompletionOutcome.Blocked, assessment.Outcome);
        Assert.Equal(11, assessment.Blockers.Count);
    }

    // AB-I8-011/AB-I8-012: com os dois SystemDerived e os quatro HumanApproval TODOS satisfeitos, a avaliação
    // permanece Blocked — os cinco critérios EvidenceDerived não têm store canônico neste repositório, então
    // NUNCA podem convergir para Eligible por meio deste caminho (prova executável de que nada é fabricado por
    // omissão, e de que uma atestação não pode contornar a ausência de um store canônico real).
    [Fact]
    public async Task EvenWithSystemDerivedAndAllHumanApprovalCriteriaSatisfiedTheEvidenceDerivedCriteriaStillBlock()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        SeedClosedReconciliation(fixtures.ReconciliationStore, scope, wave, jobName);
        SeedProviderResultsCollected(fixtures.ServiceResultStore, scope, wave, jobName);
        SeedAllAttestedCriteriaAsPass(fixtures.AttestationStore, scope);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var assessment = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(MigrationCompletionOutcome.Blocked, assessment.Outcome);
        Assert.Equal(EvidenceDerivedCriteria.Length, assessment.Blockers.Count);
        Assert.All(EvidenceDerivedCriteria, expected =>
        {
            var blocker = Assert.Single(assessment.Blockers, b => b.CriterionId.Value == expected.CriterionId);
            Assert.Equal(ReadinessControlStatus.NotMeasured, blocker.Status);
            Assert.Equal(expected.ReasonCode, blocker.ReasonCode);
        });

        // Os SystemDerived e os quatro HumanApproval de fato passaram — só os cinco EvidenceDerived bloqueiam.
        Assert.Equal(ReadinessControlStatus.Pass, assessment.CriterionResults.Single(r => r.CriterionId.Value == "COMPLETION.RECONCILIATION_CLOSED").Status);
        Assert.Equal(ReadinessControlStatus.Pass, assessment.CriterionResults.Single(r => r.CriterionId.Value == "COMPLETION.PROVIDER_RESULTS_COLLECTED").Status);
        Assert.All(AttestedCriteria, criterionIdValue =>
            Assert.Equal(ReadinessControlStatus.Pass, assessment.CriterionResults.Single(r => r.CriterionId.Value == criterionIdValue).Status));
    }

    // AB-I8-011 item 6/AB-I8-012: tentar atestar qualquer um dos cinco critérios EvidenceDerived é recusado —
    // nunca silenciosamente ignorado nem convertido em Pass — mesmo por um ator autorizado a atestar os demais.
    [Theory]
    [InlineData("COMPLETION.SOURCE_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.PARTS_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM")]
    [InlineData("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL")]
    [InlineData("COMPLETION.USERS_INACTIVE_HANDLED")]
    public void AnEvidenceDerivedCriterionCanNeverBeConstructedAsAnAttestationRegardlessOfActorPrivilege(string evidenceDerivedCriterionId)
    {
        var scope = NewScope();

        Assert.Throws<MigrationCompletionAttestationNotAllowedException>(() => MigrationCompletionCriterionAttestation.Create(
            scope.Tenant, scope.Project, new MigrationCompletionCriterionId(evidenceDerivedCriterionId), 1, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.Attested(SomeFingerprint, "administrator override attempt"), reasonCode: string.Empty,
            "admin-1", "Administrator", CorrelationId.New(), Now));
    }

    [Fact]
    public async Task ReconciliationInconclusiveBlocksEvenWhenAllOtherCriteriaPass()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var inconclusive = ReconciliationCertificate.Create(
            scope.Tenant, scope.Project, wave, jobName, 1, 1, SomeFingerprint, SomeFingerprint, ReconciliationOutcome.Inconclusive,
            totalItemCount: 10, incompleteItemCount: 0, deviationCount: 0, SomeFingerprint, SomeFingerprint, duplicateRiskDetected: false,
            "approver-1", "Approver", CorrelationId.New(), Now);
        fixtures.ReconciliationStore.Seed(scope, wave, jobName, inconclusive);
        SeedProviderResultsCollected(fixtures.ServiceResultStore, scope, wave, jobName);
        SeedAllAttestedCriteriaAsPass(fixtures.AttestationStore, scope);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var assessment = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(MigrationCompletionOutcome.Blocked, assessment.Outcome);
        var reconciliationResult = assessment.CriterionResults.Single(r => r.CriterionId.Value == "COMPLETION.RECONCILIATION_CLOSED");
        Assert.Equal(ReadinessControlStatus.Fail, reconciliationResult.Status);
    }

    [Fact]
    public async Task ReconciliationWithIncompleteEvidenceBlocksAsBlockedNotPass()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var incomplete = ReconciliationCertificate.Create(
            scope.Tenant, scope.Project, wave, jobName, 1, 1, SomeFingerprint, SomeFingerprint, ReconciliationOutcome.Pass,
            totalItemCount: 10, incompleteItemCount: 3, deviationCount: 0, SomeFingerprint, SomeFingerprint, duplicateRiskDetected: false,
            "approver-1", "Approver", CorrelationId.New(), Now);
        fixtures.ReconciliationStore.Seed(scope, wave, jobName, incomplete);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var assessment = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        var reconciliationResult = assessment.CriterionResults.Single(r => r.CriterionId.Value == "COMPLETION.RECONCILIATION_CLOSED");
        Assert.Equal(ReadinessControlStatus.Blocked, reconciliationResult.Status);
        Assert.Equal("RECONCILIATION_EVIDENCE_INCOMPLETE", reconciliationResult.ReasonCode);
    }

    [Fact]
    public async Task ADuplicateRiskCertificateBlocksReconciliationEvenWithAPassResult()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        var duplicateRisk = ReconciliationCertificate.Create(
            scope.Tenant, scope.Project, wave, jobName, 1, 1, SomeFingerprint, SomeFingerprint, ReconciliationOutcome.Pass,
            totalItemCount: 10, incompleteItemCount: 0, deviationCount: 0, SomeFingerprint, SomeFingerprint, duplicateRiskDetected: true,
            "approver-1", "Approver", CorrelationId.New(), Now);
        fixtures.ReconciliationStore.Seed(scope, wave, jobName, duplicateRisk);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var assessment = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        var reconciliationResult = assessment.CriterionResults.Single(r => r.CriterionId.Value == "COMPLETION.RECONCILIATION_CLOSED");
        Assert.Equal(ReadinessControlStatus.Fail, reconciliationResult.Status);
    }

    [Theory]
    [InlineData("COMPLETION.CUSTOMER_FINAL_APPROVAL")]
    [InlineData("COMPLETION.HOLDS_RETENTION_REVIEWED")]
    [InlineData("COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED")]
    public async Task EachIndividualAttestedCriterionMissingIndividuallyBlocksEligibility(string missingCriterionIdValue)
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        SeedClosedReconciliation(fixtures.ReconciliationStore, scope, wave, jobName);
        SeedProviderResultsCollected(fixtures.ServiceResultStore, scope, wave, jobName);
        foreach (var criterionIdValue in AttestedCriteria.Where(id => id != missingCriterionIdValue))
        {
            var criterionId = new MigrationCompletionCriterionId(criterionIdValue);
            fixtures.AttestationStore.SeedBypassingUseCase(scope, MigrationCompletionCriterionAttestation.Create(
                scope.Tenant, scope.Project, criterionId, 1, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.Attested(SomeFingerprint, $"fixture:{criterionIdValue}"), reasonCode: string.Empty,
                "approver-1", "Approver", CorrelationId.New(), Now));
        }

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var assessment = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(MigrationCompletionOutcome.Blocked, assessment.Outcome);
        Assert.Contains(assessment.Blockers, blocker => blocker.CriterionId.Value == missingCriterionIdValue);
    }

    [Fact]
    public async Task IdenticalReplayConvergesToTheSameAssessmentVersion()
    {
        var fixtures = new Fixtures();
        var scope = NewScope();
        var wave = new WaveId(Guid.NewGuid());
        var jobName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, 1);
        SeedClosedReconciliation(fixtures.ReconciliationStore, scope, wave, jobName);
        SeedProviderResultsCollected(fixtures.ServiceResultStore, scope, wave, jobName);
        SeedAllAttestedCriteriaAsPass(fixtures.AttestationStore, scope);

        var useCase = fixtures.BuildUseCase(new Canary.FakeAuthenticatedActorAccessor("alice", "Approver"));
        var first = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            new ComposeMigrationCompletionAssessmentCommand(scope, wave, jobName, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(first.AssessmentVersion, second.AssessmentVersion);
        Assert.Single(await fixtures.AssessmentStore.GetHistoryAsync(scope, CancellationToken.None));
    }
}
