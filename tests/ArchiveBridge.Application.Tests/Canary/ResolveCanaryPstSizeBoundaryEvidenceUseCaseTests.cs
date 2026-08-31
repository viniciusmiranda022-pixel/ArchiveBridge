using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using Xunit;

namespace ArchiveBridge.Application.Tests.Canary;

/// <summary>
/// AB-I8-006 — <see cref="ResolveCanaryPstSizeBoundaryEvidenceUseCase"/>: CANARY.PST_SIZE_BOUNDARY_COVERAGE
/// reclassificado de OperatorAttested para SystemDerived. Pass exige o ObservedSizeBytes REAL de duas
/// PstInspectionRecord canônicas nos dois lados dos limiares — nunca o veredito alegado pelo operador.
/// </summary>
public sealed class ResolveCanaryPstSizeBoundaryEvidenceUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly Sha256Hash SmallHash = new(new string('c', 64));
    private static readonly Sha256Hash BoundaryHash = new(new string('d', 64));
    private static readonly ArtifactId SmallArtifact = new(Guid.NewGuid());
    private static readonly ArtifactId BoundaryArtifact = new(Guid.NewGuid());
    private const long OneGib = 1024L * 1024 * 1024;

    private static async Task<(InMemoryCanaryScenarioResultStore ResultStore, InMemoryPstInspectionStore InspectionStore)> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, SomeHash, Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now, CancellationToken.None);
        return (new InMemoryCanaryScenarioResultStore(planStore), new InMemoryPstInspectionStore());
    }

    private static ResolveCanaryPstSizeBoundaryEvidenceUseCase BuildUseCase(InMemoryPstInspectionStore inspectionStore, InMemoryCanaryScenarioResultStore resultStore) =>
        new(inspectionStore, resultStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

    private static PstInspectionRecord ValidRecord(ArtifactId artifact, Sha256Hash hash, long sizeBytes) =>
        PstInspectionRecord.Complete(
            InspectionId.New(), Scope.Tenant, Scope.Project, artifact, hash, hash, sizeBytes,
            PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, "pst-engine", "1.0.0", CorrelationId.New(), Now, Now);

    private static ResolveCanaryPstSizeBoundaryEvidenceCommand Command() =>
        new(Scope, 1, SmallArtifact, SmallHash, BoundaryArtifact, BoundaryHash, CorrelationId.New());

    [Fact]
    public async Task WithNeitherInspectionTheScenarioIsNotPerformed()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
        Assert.Equal("SMALL_ARTIFACT_INSPECTION_MISSING", result.ReasonCode);
    }

    [Fact]
    public async Task WithOnlyTheBoundaryInspectionTheScenarioIsNotPerformed()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        inspectionStore.Seed(Scope, ValidRecord(BoundaryArtifact, BoundaryHash, 17L * OneGib));
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
        Assert.Equal("SMALL_ARTIFACT_INSPECTION_MISSING", result.ReasonCode);
    }

    [Fact]
    public async Task ASmallArtifactThatIsNotActuallySmallIsBlocked()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        inspectionStore.Seed(Scope, ValidRecord(SmallArtifact, SmallHash, 512L * 1024 * 1024)); // 512 MiB — não é "pequeno" o suficiente.
        inspectionStore.Seed(Scope, ValidRecord(BoundaryArtifact, BoundaryHash, 17L * OneGib));
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("SMALL_ARTIFACT_NOT_SMALL_ENOUGH", result.ReasonCode);
    }

    [Fact]
    public async Task ABoundaryArtifactBelowTheThresholdIsBlocked()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        inspectionStore.Seed(Scope, ValidRecord(SmallArtifact, SmallHash, 1024));
        inspectionStore.Seed(Scope, ValidRecord(BoundaryArtifact, BoundaryHash, 1L * OneGib)); // longe do boundary de 18 GB.
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("BOUNDARY_ARTIFACT_BELOW_THRESHOLD", result.ReasonCode);
    }

    [Fact]
    public async Task TwoGenuinelySizedArtifactsAreProvenAsPass()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        inspectionStore.Seed(Scope, ValidRecord(SmallArtifact, SmallHash, 1024));
        inspectionStore.Seed(Scope, ValidRecord(BoundaryArtifact, BoundaryHash, 17L * OneGib));
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
        Assert.Equal(CanaryEvidenceKind.SystemDerived, result.Evidence.Kind);
    }

    [Fact]
    public async Task ResolvedScenarioIsPersistedAndReadableAfterward()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(inspectionStore, resultStore);
        await useCase.ExecuteAsync(Command(), CancellationToken.None);

        var persisted = await resultStore.GetLatestAsync(Scope, 1, new CanaryScenarioId("CANARY.PST_SIZE_BOUNDARY_COVERAGE"), CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, persisted!.Status);
    }
}
