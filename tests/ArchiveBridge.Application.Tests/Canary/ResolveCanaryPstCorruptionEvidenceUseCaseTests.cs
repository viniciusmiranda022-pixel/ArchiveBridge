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
/// AB-I8-006 — <see cref="ResolveCanaryPstCorruptionEvidenceUseCase"/>: CANARY.KNOWN_CORRUPTION_QUARANTINE
/// reclassificado de OperatorAttested para SystemDerived. Pass exige uma PstInspectionRecord CANÔNICA
/// (hash bate) com StructuralDiagnostic != Valid — nunca o veredito alegado pelo operador.
/// </summary>
public sealed class ResolveCanaryPstCorruptionEvidenceUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly Sha256Hash ExpectedPstHash = new(new string('c', 64));
    private static readonly ArtifactId Artifact = new(Guid.NewGuid());

    private static async Task<(InMemoryCanaryScenarioResultStore ResultStore, InMemoryPstInspectionStore InspectionStore)> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, SomeHash, Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now, CancellationToken.None);
        return (new InMemoryCanaryScenarioResultStore(planStore), new InMemoryPstInspectionStore());
    }

    private static ResolveCanaryPstCorruptionEvidenceUseCase BuildUseCase(InMemoryPstInspectionStore inspectionStore, InMemoryCanaryScenarioResultStore resultStore) =>
        new(inspectionStore, resultStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

    private static PstInspectionRecord CompletedRecord(PstStructuralDiagnostic diagnostic) =>
        PstInspectionRecord.Complete(
            InspectionId.New(), Scope.Tenant, Scope.Project, Artifact, ExpectedPstHash, ExpectedPstHash, observedSizeBytes: 4096,
            diagnostic, PstFormatVariant.Unicode2013Plus, "pst-engine", "1.0.0", CorrelationId.New(), Now, Now);

    [Fact]
    public async Task WithNoInspectionAnywhereTheScenarioIsNotPerformed()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(Scope, 1, Artifact, ExpectedPstHash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
        Assert.Equal("PST_INSPECTION_NOT_PERFORMED", result.ReasonCode);
    }

    [Fact]
    public async Task AValidPstCannotProveCorruptionAndIsBlocked()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        inspectionStore.Seed(Scope, CompletedRecord(PstStructuralDiagnostic.Valid));
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(Scope, 1, Artifact, ExpectedPstHash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("PST_NOT_DIAGNOSED_CORRUPT", result.ReasonCode);
    }

    [Theory]
    [InlineData(PstStructuralDiagnostic.TooSmall)]
    [InlineData(PstStructuralDiagnostic.InvalidSignature)]
    [InlineData(PstStructuralDiagnostic.InvalidClientSignature)]
    [InlineData(PstStructuralDiagnostic.UnsupportedVersion)]
    public async Task ADiagnosedStructuralCorruptionIsPass(PstStructuralDiagnostic diagnostic)
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        inspectionStore.Seed(Scope, CompletedRecord(diagnostic));
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(Scope, 1, Artifact, ExpectedPstHash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
        Assert.Equal(CanaryEvidenceKind.SystemDerived, result.Evidence.Kind);
    }

    [Fact]
    public async Task CrossTenantInspectionNeverResolvesForAnotherTenant()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        var otherScope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        inspectionStore.Seed(otherScope, CompletedRecord(PstStructuralDiagnostic.InvalidSignature));
        var useCase = BuildUseCase(inspectionStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryPstCorruptionEvidenceCommand(Scope, 1, Artifact, ExpectedPstHash, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
    }

    [Fact]
    public async Task ResolvedScenarioIsPersistedAndReadableAfterward()
    {
        var (resultStore, inspectionStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(inspectionStore, resultStore);
        await useCase.ExecuteAsync(new ResolveCanaryPstCorruptionEvidenceCommand(Scope, 1, Artifact, ExpectedPstHash, CorrelationId.New()), CancellationToken.None);

        var persisted = await resultStore.GetLatestAsync(Scope, 1, new CanaryScenarioId("CANARY.KNOWN_CORRUPTION_QUARANTINE"), CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, persisted!.Status);
    }
}
