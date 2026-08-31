using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests.Canary;

/// <summary>
/// AB-I8-004 — <see cref="ResolveCanarySystemEvidenceUseCase"/>: cada cenário SystemDerived resolvido a
/// partir de evidência canônica JÁ PERSISTIDA (I5/I7); ausência de evidência NUNCA vira Pass.
/// </summary>
public sealed class ResolveCanarySystemEvidenceUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));

    private static async Task<(InMemoryCanaryScenarioResultStore ResultStore, InMemoryMailboxPrecheckStore MailboxStore, InMemoryRecoveryReadinessStore RecoveryStore)> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, SomeHash, Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now, CancellationToken.None);
        return (new InMemoryCanaryScenarioResultStore(planStore), new InMemoryMailboxPrecheckStore(), new InMemoryRecoveryReadinessStore());
    }

    private static ResolveCanarySystemEvidenceUseCase BuildUseCase(
        InMemoryMailboxPrecheckStore mailboxStore, InMemoryRecoveryReadinessStore recoveryStore, InMemoryCanaryScenarioResultStore resultStore) =>
        new(mailboxStore, recoveryStore, resultStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

    [Fact]
    public async Task WithNoEvidenceAnywhereAllThreeScenariosAreNotPerformed()
    {
        var (resultStore, mailboxStore, recoveryStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(mailboxStore, recoveryStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanarySystemEvidenceCommand(Scope, 1, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.TenantMailboxControlled.Status);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.CrashRecovery.Status);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.RestoreRollbackOperational.Status);
    }

    [Fact]
    public async Task AnActiveMailboxPrecheckResolvesToPassForTenantMailboxControlled()
    {
        var (resultStore, mailboxStore, recoveryStore) = await SeedAuthorizedPlanAsync();
        var snapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), Scope.Tenant, Scope.Project,
            new ArchiveRef("canary-test@contoso.example", TargetArchiveId.FromMailbox("canary-test@contoso.example")),
            version: 1, exchangeGuid: Guid.NewGuid(), archiveGuid: Guid.NewGuid(), MailboxArchiveStatus.Active, "UserMailbox",
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false, archiveItemCount: 10,
            archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000, Now, CorrelationId.New(), Now);
        mailboxStore.Seed(Scope, snapshot);
        var useCase = BuildUseCase(mailboxStore, recoveryStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanarySystemEvidenceCommand(Scope, 1, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.TenantMailboxControlled.Status);
    }

    [Fact]
    public async Task ANonActiveMailboxPrecheckIsBlockedNeverPass()
    {
        var (resultStore, mailboxStore, recoveryStore) = await SeedAuthorizedPlanAsync();
        var snapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), Scope.Tenant, Scope.Project,
            new ArchiveRef("canary-test@contoso.example", TargetArchiveId.FromMailbox("canary-test@contoso.example")),
            version: 1, exchangeGuid: Guid.NewGuid(), archiveGuid: Guid.NewGuid(), MailboxArchiveStatus.Disabled, "UserMailbox",
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false, archiveItemCount: 10,
            archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000, Now, CorrelationId.New(), Now);
        mailboxStore.Seed(Scope, snapshot);
        var useCase = BuildUseCase(mailboxStore, recoveryStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanarySystemEvidenceCommand(Scope, 1, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.TenantMailboxControlled.Status);
    }

    [Fact]
    public async Task APassingPendingWorkRebuildExerciseResolvesToPassForCrashRecovery()
    {
        var (resultStore, mailboxStore, recoveryStore) = await SeedAuthorizedPlanAsync();
        var measurement = new RecoveryObjectiveMeasurement(Now, Now.AddMinutes(5));
        var record = RecoveryReadinessRecord.Pass(
            Scope.Tenant, Scope.Project, RecoveryExerciseType.PendingWorkRebuild, exerciseVersion: 1, RecoveryObjective.None,
            objectiveThreshold: null, measurement, SomeHash, notes: "rebuild converged", "svc-recovery", "ServiceAccount",
            CorrelationId.New(), Now);
        recoveryStore.Seed(Scope, RecoveryExerciseType.PendingWorkRebuild, record);
        var useCase = BuildUseCase(mailboxStore, recoveryStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanarySystemEvidenceCommand(Scope, 1, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.CrashRecovery.Status);
    }

    [Fact]
    public async Task ResolvedScenariosArePersistedAndReadableAfterward()
    {
        var (resultStore, mailboxStore, recoveryStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(mailboxStore, recoveryStore, resultStore);
        await useCase.ExecuteAsync(new ResolveCanarySystemEvidenceCommand(Scope, 1, CorrelationId.New()), CancellationToken.None);

        var persisted = await resultStore.GetLatestAsync(Scope, 1, new CanaryScenarioId("CANARY.CRASH_RECOVERY"), CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, persisted!.Status);
    }
}
