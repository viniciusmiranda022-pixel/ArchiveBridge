using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests.Canary;

/// <summary>
/// AB-I8-006 — <see cref="ResolveCanaryTargetRootGuardEvidenceUseCase"/>: CANARY.DIFFERENT_TARGET_ROOT_BLOCKS
/// reclassificado de OperatorAttested para SystemDerived. Exercita o MESMO guard de domínio real que protege
/// produção (<see cref="MigrationWave.ChangeTargetRootFolder"/>) — nunca aceita o veredito alegado pelo
/// operador.
/// </summary>
public sealed class ResolveCanaryTargetRootGuardEvidenceUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly TargetRootFolder OriginalRoot = TargetRootFolder.ForWave("ProjA", "Wave1");
    private static readonly TargetRootFolder DifferentRoot = TargetRootFolder.ForWave("ProjA", "Wave2");

    private static WaveSelection Selection() =>
        new([new WaveEntry("/src/0/a.pst", "a.pst", new ArchiveRef("user0@contoso.com"), 10, 1)]);

    private static MigrationWave NewDraftWave() =>
        MigrationWave.Create(WaveId.New(), Scope.Tenant, Scope.Project, new WaveName("Onda Canário"), OriginalRoot, SomeHash, Selection(), Now);

    private static MigrationWave NewApprovedWave()
    {
        var wave = NewDraftWave();
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("approver-1", Now);
        return wave;
    }

    private static async Task<(InMemoryCanaryScenarioResultStore ResultStore, InMemoryWaveStore WaveStore)> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, SomeHash, Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now, CancellationToken.None);
        return (new InMemoryCanaryScenarioResultStore(planStore), new InMemoryWaveStore());
    }

    private static ResolveCanaryTargetRootGuardEvidenceUseCase BuildUseCase(InMemoryWaveStore waveStore, InMemoryCanaryScenarioResultStore resultStore) =>
        new(waveStore, resultStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

    [Fact]
    public async Task WithNoWaveTheScenarioIsNotPerformed()
    {
        var (resultStore, waveStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(waveStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryTargetRootGuardEvidenceCommand(Scope, 1, WaveId.New(), DifferentRoot, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
    }

    [Fact]
    public async Task AnApprovedWaveRejectsADifferentRootAndIsPass()
    {
        var (resultStore, waveStore) = await SeedAuthorizedPlanAsync();
        var wave = NewApprovedWave();
        waveStore.Seed(Scope, wave);
        var useCase = BuildUseCase(waveStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryTargetRootGuardEvidenceCommand(Scope, 1, wave.Id, DifferentRoot, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
        Assert.Equal(CanaryEvidenceKind.SystemDerived, result.Evidence.Kind);
    }

    [Fact]
    public async Task ADraftWaveCannotYetProveBlockingAndIsBlocked()
    {
        var (resultStore, waveStore) = await SeedAuthorizedPlanAsync();
        var wave = NewDraftWave();
        waveStore.Seed(Scope, wave);
        var useCase = BuildUseCase(waveStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryTargetRootGuardEvidenceCommand(Scope, 1, wave.Id, DifferentRoot, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("WAVE_SELECTION_STILL_MUTABLE", result.ReasonCode);
    }

    [Fact]
    public async Task ARootIdenticalToTheCurrentOneProvesNothingAndIsBlocked()
    {
        var (resultStore, waveStore) = await SeedAuthorizedPlanAsync();
        var wave = NewApprovedWave();
        waveStore.Seed(Scope, wave);
        var useCase = BuildUseCase(waveStore, resultStore);

        var result = await useCase.ExecuteAsync(
            new ResolveCanaryTargetRootGuardEvidenceCommand(Scope, 1, wave.Id, OriginalRoot, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("ATTEMPTED_ROOT_NOT_ACTUALLY_DIFFERENT", result.ReasonCode);
    }

    [Fact]
    public async Task TheGuardDoesNotPersistAnyMutationToTheWaveStore()
    {
        var (resultStore, waveStore) = await SeedAuthorizedPlanAsync();
        var wave = NewApprovedWave();
        waveStore.Seed(Scope, wave);
        var useCase = BuildUseCase(waveStore, resultStore);

        await useCase.ExecuteAsync(new ResolveCanaryTargetRootGuardEvidenceCommand(Scope, 1, wave.Id, DifferentRoot, CorrelationId.New()), CancellationToken.None);

        var rereadWave = await waveStore.GetAsync(Scope, wave.Id, CancellationToken.None);
        Assert.Equal(OriginalRoot, rereadWave!.TargetRootFolder);
    }
}
