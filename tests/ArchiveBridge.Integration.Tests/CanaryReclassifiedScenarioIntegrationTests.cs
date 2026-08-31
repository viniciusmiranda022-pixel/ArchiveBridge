using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Canary;
using ArchiveBridge.Infrastructure.ProductionReadiness;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I8-006 (SQL Server real) — os dois cenários reclassificados de OperatorAttested para SystemDerived cuja
/// evidência canônica vem de <see cref="ArchiveBridge.Contracts.PstProcessing.IPstInspectionStore"/>
/// (CANARY.KNOWN_CORRUPTION_QUARANTINE, CANARY.PST_SIZE_BOUNDARY_COVERAGE): resolvidos a partir de
/// PstInspectionRecord REALMENTE persistidas via a store SQL real (mesmo mecanismo já homologado pelo Slice
/// 4B), nunca do veredito alegado pelo operador; anti-IDOR cross-tenant garantido pela mesma RLS que protege
/// <see cref="SqlPstInspectionStore"/>.
/// <para>
/// CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT e CANARY.DIFFERENT_TARGET_ROOT_BLOCKS (evidência de
/// <c>IPurviewUploadAttemptStore</c>/<c>IWaveStore</c>) têm cobertura completa de todos os ramos de decisão
/// em <c>ResolveCanaryReplayIdempotencyEvidenceUseCaseTests</c>/<c>ResolveCanaryTargetRootGuardEvidenceUseCaseTests</c>
/// (Application.Tests, com duplos fiéis às stores reais) — SQL-real end-to-end para esses dois fica para um
/// Passo futuro que já precise construir o cenário completo de wave/job/upload-request necessário (fora do
/// escopo mínimo de AB-I8-006); as próprias stores SQL subjacentes (<c>SqlPurviewUploadAttemptStore</c>,
/// <c>SqlWaveStore</c>) já têm cobertura SQL-real própria de AB-I5-009/Slice 2, não reexercitada aqui.
/// </para>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class CanaryReclassifiedScenarioIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private const string ValidCommitSha = "0123456789abcdef0123456789abcdef01234567";
    private const long OneGib = 1024L * 1024 * 1024;

    private static readonly IAuthenticatedActorAccessor ApproverActor =
        new FakeAuthenticatedActorAccessor("approver-1@contoso.com", PortalRoles.Approver);

    private static readonly IAuthenticatedActorAccessor OperatorActor =
        new FakeAuthenticatedActorAccessor("operator-1@contoso.com", PortalRoles.Operator);

    private SqlProductionReadinessReviewStore Readiness() => new(fixture.Factory);

    private SqlCanaryPlanStore Plans() => new(fixture.Factory);

    private SqlCanaryScenarioResultStore Results() => new(fixture.Factory);

    private SqlPstInspectionStore Inspections() => new(fixture.Factory);

    private SqlPstCustodyStore Custody() => new(fixture.Factory, Clock);

    private AuthorizeCanaryPlanUseCase AuthorizeUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Readiness(), Plans(), Clock, actor ?? ApproverActor);

    private ResolveCanaryPstCorruptionEvidenceUseCase CorruptionUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Inspections(), Results(), Clock, actor ?? OperatorActor);

    private ResolveCanaryPstSizeBoundaryEvidenceUseCase SizeBoundaryUseCase(IAuthenticatedActorAccessor? actor = null) =>
        new(Inspections(), Results(), Clock, actor ?? OperatorActor);

    private sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    /// <summary>Mesma fixture de AB-I8-004: Production Readiness Review ReadyForCanary via a store REAL.</summary>
    private async Task<int> AuthorizeCanaryPlanAsync(TenantScope scope)
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Clock.UtcNow);
        }

        await Readiness().RecordReviewAsync(
            scope, ValidCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved, "svc-readiness", "Administrator",
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None);
        var plan = await AuthorizeUseCase().ExecuteAsync(new AuthorizeCanaryPlanCommand(scope, CorrelationId.New()), CancellationToken.None);
        return plan.PlanVersion;
    }

    /// <summary>
    /// Registra a custódia REAL do artefato (obrigatório pela FK composta
    /// <c>FK_pst_inspections_artifact -&gt; dbo.pst_artifacts</c>, mesmo mecanismo homologado pela Slice 4B) e só
    /// então grava a inspeção apontando para o artefato recém-registrado. Retorna o <see cref="ArtifactId"/>
    /// realmente emitido pela store — nunca um Guid fabricado pelo teste, porque
    /// <see cref="ArchiveBridge.Contracts.PstProcessing.IPstCustodyStore.RegisterAsync"/> sempre emite sua
    /// própria identidade, exatamente como em produção.
    /// </summary>
    private async Task<ArtifactId> SaveInspectionAsync(
        TenantScope scope, Sha256Hash hash, long sizeBytes, PstStructuralDiagnostic diagnostic)
    {
        var relativePath = new PstRelativePath($"canary/{Guid.NewGuid():N}.pst");
        var registered = await Custody().RegisterAsync(scope.Tenant, scope.Project, relativePath, hash, sizeBytes, CancellationToken.None);
        await Inspections().SaveAsync(
            PstInspectionRecord.Complete(
                InspectionId.New(), scope.Tenant, scope.Project, registered.Id, hash, hash, sizeBytes, diagnostic,
                PstFormatVariant.Unicode2013Plus, "pst-engine", "1.0.0", CorrelationId.New(), Clock.UtcNow, Clock.UtcNow),
            CancellationToken.None);
        return registered.Id;
    }

    [Fact]
    public async Task KnownCorruptionAgainstARealCanonicalInspectionNeverBecomesPassWithoutAQuarantineMechanism()
    {
        // AB-I8-007: nenhum mecanismo de quarantine existe neste repositório — mesmo com uma
        // PstInspectionRecord CANÔNICA REAL (SQL Server) diagnosticada corrupta, o cenário permanece
        // Blocked, nunca Pass.
        var scope = SqlServerFixture.NewScope();
        var planVersion = await AuthorizeCanaryPlanAsync(scope);
        var hash = new Sha256Hash(new string('c', 64));
        var artifact = await SaveInspectionAsync(scope, hash, 4096, PstStructuralDiagnostic.InvalidSignature);

        var result = await CorruptionUseCase().ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(scope, planVersion, artifact, hash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("CORRUPTION_DIAGNOSED_BUT_NO_QUARANTINE_MECHANISM", result.ReasonCode);
        var persisted = await Results().GetLatestAsync(scope, planVersion, new CanaryScenarioId("CANARY.KNOWN_CORRUPTION_QUARANTINE"), CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(CanaryScenarioStatus.Blocked, persisted!.Status);
    }

    [Fact]
    public async Task AStructurallyValidPstNeverResolvesToPassEvenAgainstARealCanonicalInspection()
    {
        var scope = SqlServerFixture.NewScope();
        var planVersion = await AuthorizeCanaryPlanAsync(scope);
        var hash = new Sha256Hash(new string('d', 64));
        var artifact = await SaveInspectionAsync(scope, hash, 4096, PstStructuralDiagnostic.Valid);

        var result = await CorruptionUseCase().ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(scope, planVersion, artifact, hash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
    }

    [Fact]
    public async Task AnotherTenantsInspectionNeverResolvesCorruptionEvenWithTheSameArtifactAndHash()
    {
        var ownerScope = SqlServerFixture.NewScope();
        var hash = new Sha256Hash(new string('e', 64));
        var artifact = await SaveInspectionAsync(ownerScope, hash, 4096, PstStructuralDiagnostic.InvalidSignature);

        var otherScope = SqlServerFixture.NewScope();
        var planVersion = await AuthorizeCanaryPlanAsync(otherScope);

        var result = await CorruptionUseCase().ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(otherScope, planVersion, artifact, hash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
    }

    [Fact]
    public async Task PstSizeBoundaryAgainstTwoRealCanonicalInspectionsNeverBecomesPassBecauseSmallPstHasNoDocumentedThreshold()
    {
        // AB-I8-007: mesmo com o lado "boundary" genuinamente provado (>= PartitionPolicy.RunbookTargetPartBytes,
        // o único limiar de 18 GB REALMENTE documentado) contra a store SQL real, o cenário nunca vira Pass —
        // "PST pequeno" não tem nenhum limiar numérico documentado em lugar algum.
        var scope = SqlServerFixture.NewScope();
        var planVersion = await AuthorizeCanaryPlanAsync(scope);
        var smallHash = new Sha256Hash(new string('1', 64));
        var boundaryHash = new Sha256Hash(new string('2', 64));
        var smallArtifact = await SaveInspectionAsync(scope, smallHash, 1024, PstStructuralDiagnostic.Valid);
        var boundaryArtifact = await SaveInspectionAsync(scope, boundaryHash, 19L * OneGib, PstStructuralDiagnostic.Valid);

        var result = await SizeBoundaryUseCase().ExecuteAsync(
            new ResolveCanaryPstSizeBoundaryEvidenceCommand(scope, planVersion, smallArtifact, smallHash, boundaryArtifact, boundaryHash, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("SMALL_PST_THRESHOLD_UNDOCUMENTED", result.ReasonCode);
        var persisted = await Results().GetLatestAsync(scope, planVersion, new CanaryScenarioId("CANARY.PST_SIZE_BOUNDARY_COVERAGE"), CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(CanaryScenarioStatus.Blocked, persisted!.Status);
    }

    [Fact]
    public async Task PstSizeBoundaryStaysBlockedWhenTheBoundaryArtifactIsNotActuallyNearTheBoundary()
    {
        var scope = SqlServerFixture.NewScope();
        var planVersion = await AuthorizeCanaryPlanAsync(scope);
        var smallHash = new Sha256Hash(new string('3', 64));
        var boundaryHash = new Sha256Hash(new string('4', 64));
        var smallArtifact = await SaveInspectionAsync(scope, smallHash, 1024, PstStructuralDiagnostic.Valid);
        var boundaryArtifact = await SaveInspectionAsync(scope, boundaryHash, 1L * OneGib, PstStructuralDiagnostic.Valid);

        var result = await SizeBoundaryUseCase().ExecuteAsync(
            new ResolveCanaryPstSizeBoundaryEvidenceCommand(scope, planVersion, smallArtifact, smallHash, boundaryArtifact, boundaryHash, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("BOUNDARY_ARTIFACT_BELOW_THRESHOLD", result.ReasonCode);
    }

    [Fact]
    public async Task AViewerCannotResolveCorruptionEvidenceThroughTheRealStore()
    {
        var scope = SqlServerFixture.NewScope();
        var planVersion = await AuthorizeCanaryPlanAsync(scope);
        var hash = new Sha256Hash(new string('f', 64));
        var artifact = await SaveInspectionAsync(scope, hash, 4096, PstStructuralDiagnostic.InvalidSignature);
        var viewerActor = new FakeAuthenticatedActorAccessor("viewer-1@contoso.com", PortalRoles.Viewer);

        await Assert.ThrowsAsync<CanaryAuthorizationException>(() => CorruptionUseCase(viewerActor).ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(scope, planVersion, artifact, hash, CorrelationId.New()), CancellationToken.None));

        Assert.Null(await Results().GetLatestAsync(scope, planVersion, new CanaryScenarioId("CANARY.KNOWN_CORRUPTION_QUARANTINE"), CancellationToken.None));
    }
}
