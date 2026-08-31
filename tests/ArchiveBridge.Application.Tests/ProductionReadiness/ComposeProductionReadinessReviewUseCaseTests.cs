using ArchiveBridge.Application.ProductionReadiness;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using Xunit;

namespace ArchiveBridge.Application.Tests.ProductionReadiness;

/// <summary>
/// AB-I8-001 — <see cref="ComposeProductionReadinessReviewUseCase"/>: RBAC server-side (nunca do payload),
/// nenhum controle SystemDerived fabricado como Pass sem evidência real, atestação ilegítima de um controle
/// SystemDerived (injetada diretamente na store, bypassando o use case de submissão) é ignorada por defesa
/// em profundidade, e replay idêntico converge sem gerar uma versão nova.
/// </summary>
public sealed class ComposeProductionReadinessReviewUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static readonly AzCopyHomologationCatalog HomologatedCatalog =
        new([new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('b', 64)))]);

    private static ComposeProductionReadinessReviewUseCase BuildUseCase(
        Contracts.Abstractions.IAuthenticatedActorAccessor actorAccessor,
        InMemoryPenTestReadinessStore? penTestStore = null,
        InMemoryWorkerHardeningBaselineStore? hardeningStore = null,
        InMemoryWdacPolicyEvidenceStore? wdacStore = null,
        InMemoryIncidentResponseDrillStore? incidentStore = null,
        InMemoryBuildProvenanceStore? buildStore = null,
        InMemoryRecoveryReadinessStore? recoveryStore = null,
        InMemoryCapabilityEvidenceStore? capabilityStore = null,
        InMemoryMailboxPrecheckStore? mailboxPrecheckStore = null,
        InMemoryMappingValidationStore? mappingValidationStore = null,
        InMemoryPurviewUploadAttemptStore? uploadAttemptStore = null,
        AzCopyHomologationCatalog? homologatedCatalog = null,
        InMemoryReadinessControlAttestationStore? attestationStore = null,
        InMemoryProductionReadinessReviewStore? reviewStore = null) =>
        new(
            penTestStore ?? new InMemoryPenTestReadinessStore(),
            hardeningStore ?? new InMemoryWorkerHardeningBaselineStore(),
            wdacStore ?? new InMemoryWdacPolicyEvidenceStore(),
            incidentStore ?? new InMemoryIncidentResponseDrillStore(),
            buildStore ?? new InMemoryBuildProvenanceStore(),
            recoveryStore ?? new InMemoryRecoveryReadinessStore(),
            capabilityStore ?? new InMemoryCapabilityEvidenceStore(),
            mailboxPrecheckStore ?? new InMemoryMailboxPrecheckStore(),
            mappingValidationStore ?? new InMemoryMappingValidationStore(),
            uploadAttemptStore ?? new InMemoryPurviewUploadAttemptStore(),
            homologatedCatalog ?? HomologatedCatalog,
            attestationStore ?? new InMemoryReadinessControlAttestationStore(),
            reviewStore ?? new InMemoryProductionReadinessReviewStore(),
            new FixedClock(Now),
            actorAccessor);

    private static ComposeProductionReadinessReviewCommand Command(TenantScope scope) =>
        new(scope, ValidCommitSha, SomeFingerprint, "ArchiveBridge.ControlPlane", CorrelationId.New());

    [Fact]
    public async Task AnonymousActorIsRejectedBeforeAnyScopedAccess()
    {
        var useCase = BuildUseCase(new UnauthenticatedActorAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecuteAsync(Command(NewScope()), CancellationToken.None));
    }

    [Fact]
    public async Task AViewerRoleCannotComposeAReview()
    {
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Viewer"));

        await Assert.ThrowsAsync<ProductionReadinessAuthorizationException>(() => useCase.ExecuteAsync(Command(NewScope()), CancellationToken.None));
    }

    [Fact]
    public async Task AnApproverCanComposeAReviewAndNothingIsFabricatedAsPass()
    {
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Approver"));

        var snapshot = await useCase.ExecuteAsync(Command(NewScope()), CancellationToken.None);

        // Nenhuma evidência real foi seedada em nenhum store — o desfecho tem que ser NotReady, nunca
        // ReadyForCanary "por acidente".
        Assert.Equal(ProductionReadinessOutcome.NotReady, snapshot.Outcome);
        Assert.NotEmpty(snapshot.Blockers);
        Assert.Equal("alice", snapshot.SubmittedBy);
        Assert.Equal("Approver", snapshot.SubmittedByRole);
    }

    [Fact]
    public async Task AnIllegitimateSystemDerivedAttestationInjectedDirectlyIntoTheStoreIsIgnored()
    {
        var scope = NewScope();
        var attestationStore = new InMemoryReadinessControlAttestationStore();

        // Bypassa SubmitReadinessControlAttestationUseCase (que recusaria isto) e injeta diretamente uma
        // "atestação" Pass para um controle SystemDerived — simula dado legado/corrompido/um caminho de
        // escrita não autorizado.
        var illegitimate = Domain.ProductionReadiness.ReadinessControlAttestation.Create(
            scope.Tenant, scope.Project, new ReadinessControlId("ARCH.ADR_APPROVED"), attestationVersion: 1,
            ReadinessControlStatus.Pass, Domain.ProductionReadiness.ReadinessEvidenceReference.Attested(SomeFingerprint, "legit-attestation"),
            reasonCode: string.Empty, "human", "Approver", CorrelationId.New(), Now);
        attestationStore.SeedBypassingUseCase(scope, illegitimate);

        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Approver"), attestationStore: attestationStore);
        var snapshot = await useCase.ExecuteAsync(Command(scope), CancellationToken.None);

        // ARCH.ADR_APPROVED É Attested — a atestação legítima deve contar.
        var adrResult = snapshot.ControlResults.Single(r => r.ControlId.Value == "ARCH.ADR_APPROVED");
        Assert.Equal(ReadinessControlStatus.Pass, adrResult.Status);

        // Mas SEC.PENTEST_NO_OPEN_CRITICAL_HIGH é SystemDerived — mesmo que alguém tentasse a mesma
        // manobra para ele, o resolver SystemDerived nunca consulta a attestation store para esse controle.
        var pentestResult = snapshot.ControlResults.Single(r => r.ControlId.Value == "SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");
        Assert.NotEqual(ReadinessControlStatus.Pass, pentestResult.Status);
    }

    [Fact]
    public async Task IdenticalReplayConvergesToTheSameReviewVersion()
    {
        var scope = NewScope();
        var reviewStore = new InMemoryProductionReadinessReviewStore();
        var useCase = BuildUseCase(new FakeAuthenticatedActorAccessor("alice", "Approver"), reviewStore: reviewStore);

        var first = await useCase.ExecuteAsync(Command(scope), CancellationToken.None);
        var second = await useCase.ExecuteAsync(Command(scope), CancellationToken.None);

        Assert.Equal(first.ReviewVersion, second.ReviewVersion);
        Assert.Equal(first.ReviewFingerprint, second.ReviewFingerprint);
        Assert.Equal(2, reviewStore.RecordCallCount);
        Assert.Single(await reviewStore.GetHistoryAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task MaterialEvidenceChangeProducesASupersedingVersion()
    {
        var scope = NewScope();
        var reviewStore = new InMemoryProductionReadinessReviewStore();
        var penTestStore = new InMemoryPenTestReadinessStore();
        var useCase = BuildUseCase(
            new FakeAuthenticatedActorAccessor("alice", "Approver"), penTestStore: penTestStore, reviewStore: reviewStore);

        var before = await useCase.ExecuteAsync(Command(scope), CancellationToken.None);

        // Muda evidência real (pen-test bundle recém preparado) entre as duas composições.
        penTestStore.Seed(scope, Domain.Security.PenTestReadinessBundle.Blocked(
            scope.Tenant, scope.Project, bundleVersion: 1, "scope", "attack-surface", "trust-boundaries", "fixtures",
            "known-blocked", SomeFingerprint, "no tester contracted yet", "svc-security", "ServiceAccount", CorrelationId.New(), Now));

        var after = await useCase.ExecuteAsync(Command(scope), CancellationToken.None);

        Assert.NotEqual(before.ReviewFingerprint, after.ReviewFingerprint);
        Assert.True(after.ReviewVersion > before.ReviewVersion);
        Assert.Equal(2, (await reviewStore.GetHistoryAsync(scope, CancellationToken.None)).Count);
    }
}
