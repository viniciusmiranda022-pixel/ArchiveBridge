using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Slice 4B, Passo 2 — o caso de uso de planejamento resolve toda identidade/custódia SERVER-SIDE, exige
/// inspeção canônica coerente com o hash de custódia, é read-only por construção (não recebe nenhuma porta
/// capaz de abrir/escrever o PST), reaproveita idempotentemente o plano canônico e falha fechado em
/// corrida/canonicidade violada. Duplos de teste implementam as portas diretamente — sem Infrastructure.
/// </summary>
public sealed class PlanPstPartitionUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly PartitionPolicy SmallPolicy = PartitionPolicy.Create(4096, 8192);

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static MigrationArtifact Custody(TenantScope scope, Sha256Hash hash) =>
        MigrationArtifact.Register(scope.Tenant, scope.Project, new PstRelativePath("a.pst"), hash, 100, Now);

    private static PstInspectionRecord Canonical(TenantScope scope, ArtifactId artifact, Sha256Hash hash, long size) =>
        PstInspectionRecord.Complete(
            InspectionId.New(), scope.Tenant, scope.Project, artifact, hash, hash, size,
            PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, "Engine", "1.0",
            CorrelationId.New(), Now, Now);

    private static PlanPstPartitionUseCase UseCase(
        FakePstCustodyStore custodyStore,
        FakePstInspectionStore inspectionStore,
        FakePartitionPlanStore planStore,
        PartitionPolicy? policy = null) =>
        new(custodyStore, inspectionStore, planStore, new TestPartitionPlanner(policy ?? SmallPolicy), new StubClock(Now));

    [Fact]
    public async Task NonexistentArtifactFailsClosedWithoutPlanningOrSaving()
    {
        var scope = NewScope();
        var custodyStore = new FakePstCustodyStore(scope: null, artifact: null);
        var inspectionStore = new FakePstInspectionStore();
        var planStore = new FakePartitionPlanStore();

        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            UseCase(custodyStore, inspectionStore, planStore)
                .ExecuteAsync(scope, ArtifactId.New(), CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, inspectionStore.FindCanonicalCount);
        Assert.Equal(0, planStore.SaveCount);
    }

    [Fact]
    public async Task CrossTenantAndCrossProjectAreIndistinguishableFromNotFound()
    {
        var ownerScope = NewScope();
        var hash = Hash("owned");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(ownerScope, artifact, Custody(ownerScope, hash));
        var inspectionStore = new FakePstInspectionStore(Canonical(ownerScope, artifact, hash, 1024));
        var planStore = new FakePartitionPlanStore();
        var useCase = UseCase(custodyStore, inspectionStore, planStore);

        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() => useCase.ExecuteAsync(
            new TenantScope(ownerScope.Tenant, new ProjectId(Guid.NewGuid())), artifact, CorrelationId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() => useCase.ExecuteAsync(
            new TenantScope(new TenantId(Guid.NewGuid()), ownerScope.Project), artifact, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, planStore.SaveCount);
    }

    [Fact]
    public async Task TheCanonicalInspectionIsAlwaysLookedUpByTheRegisteredCustodyHash()
    {
        var scope = NewScope();
        var registered = Hash("registered");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, registered));
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, registered, 1024));
        var planStore = new FakePartitionPlanStore();

        await UseCase(custodyStore, inspectionStore, planStore)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        // Staleness fail-closed: se o artefato mudar, a busca por ESTE hash não encontra canônico e o plano
        // nasce Blocked — nunca sobre uma inspeção obsoleta.
        Assert.Equal(registered, Assert.Single(inspectionStore.RequestedHashes));
    }

    [Fact]
    public async Task ArtifactWithinTargetIsPlannedAndPersistedAsCanonical()
    {
        var scope = NewScope();
        var hash = Hash("small");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, hash, 4096));
        var planStore = new FakePartitionPlanStore();

        var plan = await UseCase(custodyStore, inspectionStore, planStore)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Planned, plan.Outcome);
        Assert.True(plan.IsCanonical);
        Assert.Equal(1, planStore.SaveCount);
        Assert.True(Assert.Single(plan.Parts).CoversEntireSource);
    }

    [Fact]
    public async Task ExistingCanonicalPlanIsReplayedWithoutPersistingAgain()
    {
        var scope = NewScope();
        var hash = Hash("replay");
        var artifact = ArtifactId.New();
        var custody = Custody(scope, hash);
        var inspection = Canonical(scope, artifact, hash, 4096);
        var custodyStore = new FakePstCustodyStore(scope, artifact, custody);
        var inspectionStore = new FakePstInspectionStore(inspection);
        var planStore = new FakePartitionPlanStore();
        var useCase = UseCase(custodyStore, inspectionStore, planStore);

        var first = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id); // mesma linha canônica — nenhum efeito duplicado.
        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(1, planStore.SaveCount);
    }

    [Fact]
    public async Task ChangingThePolicyNeverReusesThePlanOfThePreviousPolicy()
    {
        var scope = NewScope();
        var hash = Hash("policy-change");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, hash, 4096));
        var planStore = new FakePartitionPlanStore();

        var underFirstPolicy = await UseCase(custodyStore, inspectionStore, planStore)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        var underSecondPolicy = await UseCase(custodyStore, inspectionStore, planStore, PartitionPolicy.Create(5000, 9000))
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.NotEqual(underFirstPolicy.PlanHash, underSecondPolicy.PlanHash);
        Assert.NotEqual(underFirstPolicy.Id, underSecondPolicy.Id);
        Assert.Equal(2, planStore.SaveCount); // o plano da política anterior NÃO foi reaproveitado.
    }

    [Fact]
    public async Task WithoutACanonicalInspectionThePlanIsBlockedEvidenceAndNeverCanonical()
    {
        var scope = NewScope();
        var hash = Hash("uninspected");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(); // nenhuma inspeção canônica
        var planStore = new FakePartitionPlanStore();
        var useCase = UseCase(custodyStore, inspectionStore, planStore);

        var first = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Blocked, first.Outcome);
        Assert.Equal(PartitionPlanReason.CanonicalInspectionUnavailable, first.Reason);
        Assert.False(first.IsCanonical);
        Assert.Empty(first.Parts);
        // Evidência append-only: cada avaliação é sua própria linha; nada é reaproveitado como canônico e o
        // índice único de canônicos nunca é consultado nem ocupado.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, planStore.SaveCount);
        Assert.Equal(0, planStore.FindCanonicalCount);
    }

    [Fact]
    public async Task AboveTheTargetThePlanIsUnsupportedEvidenceWithoutInventedBoundaries()
    {
        var scope = NewScope();
        var hash = Hash("oversized");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, hash, 4097));
        var planStore = new FakePartitionPlanStore();

        var plan = await UseCase(custodyStore, inspectionStore, planStore)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PartitionPlanOutcome.Unsupported, plan.Outcome);
        Assert.Equal(PartitionPlanReason.ItemInventoryUnavailable, plan.Reason);
        Assert.Empty(plan.Parts);
        Assert.False(plan.IsCanonical);
        Assert.Equal(0, planStore.FindCanonicalCount);
    }

    [Fact]
    public async Task ANonCanonicalRecordReturnedAsCanonicalInspectionFailsClosed()
    {
        var scope = NewScope();
        var hash = Hash("bad-store");
        var artifact = ArtifactId.New();
        var stale = PstInspectionRecord.Stale(
            InspectionId.New(), scope.Tenant, scope.Project, artifact, hash, Hash("drifted"), 4096,
            "Engine", "1.0", CorrelationId.New(), Now, Now);
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(stale);
        var planStore = new FakePartitionPlanStore();

        await Assert.ThrowsAsync<PstInspectionCanonicityViolationException>(() =>
            UseCase(custodyStore, inspectionStore, planStore)
                .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, planStore.SaveCount);
    }

    [Fact]
    public async Task ANonCanonicalPlanReturnedByThePlanStoreFailsClosed()
    {
        var scope = NewScope();
        var hash = Hash("bad-plan-store");
        var artifact = ArtifactId.New();
        var custody = Custody(scope, hash);
        var blocked = PartitionPlan.Create(
            PartitionPlanId.New(), scope.Tenant, scope.Project,
            new PartitionPlanSource(artifact, null, hash, null, null, null), SmallPolicy,
            new PartitionPlannerIdentity("TestPlanner", "1.0"), Hash("whatever"),
            PartitionPlanReason.CanonicalInspectionUnavailable, [], CorrelationId.New(), Now);
        var custodyStore = new FakePstCustodyStore(scope, artifact, custody);
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, hash, 4096));
        var planStore = new FakePartitionPlanStore(canonical: blocked);

        await Assert.ThrowsAsync<PartitionPlanCanonicityViolationException>(() =>
            UseCase(custodyStore, inspectionStore, planStore)
                .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, planStore.SaveCount);
    }

    [Fact]
    public async Task APlanWithADifferentIdentityReturnedAsCanonicalFailsClosed()
    {
        var scope = NewScope();
        var hash = Hash("wrong-identity");
        var artifact = ArtifactId.New();
        var mismatched = PartitionPlan.Create(
            PartitionPlanId.New(), scope.Tenant, scope.Project,
            new PartitionPlanSource(artifact, InspectionId.New(), hash, 10, "Engine", "1.0"), SmallPolicy,
            new PartitionPlannerIdentity("TestPlanner", "1.0"), Hash("identity-of-another-input"),
            PartitionPlanReason.SinglePartWithinTarget,
            [new PartitionPlanPart(PartitionPlanPartId.New(), 1, Hash("part"), 10, true)],
            CorrelationId.New(), Now);
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, hash, 4096));
        var planStore = new FakePartitionPlanStore(canonical: mismatched);

        await Assert.ThrowsAsync<PartitionPlanCanonicityViolationException>(() =>
            UseCase(custodyStore, inspectionStore, planStore)
                .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task AWriteConflictIsResolvedByRereadingTheCanonicalPlan()
    {
        var scope = NewScope();
        var hash = Hash("race");
        var artifact = ArtifactId.New();
        var custody = Custody(scope, hash);
        var inspection = Canonical(scope, artifact, hash, 4096);
        var planner = new TestPartitionPlanner(SmallPolicy);
        var winner = planner.Plan(custody, inspection, CorrelationId.New(), Now);

        var custodyStore = new FakePstCustodyStore(scope, artifact, custody);
        var inspectionStore = new FakePstInspectionStore(inspection);
        var planStore = new FakePartitionPlanStore(canonicalAfterConflict: winner, throwOnFirstSave: true);

        var result = await UseCase(custodyStore, inspectionStore, planStore)
            .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(winner.Id, result.Id); // devolve o canônico DA CORRIDA, nunca o registro em memória.
    }

    [Fact]
    public async Task AWriteConflictWithNoCanonicalOnRereadFailsClosedInsteadOfReturningAnUnpersistedPlan()
    {
        var scope = NewScope();
        var hash = Hash("unresolved");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore(Canonical(scope, artifact, hash, 4096));
        var planStore = new FakePartitionPlanStore(throwOnFirstSave: true);

        await Assert.ThrowsAsync<PartitionPlanConflictUnresolvedException>(() =>
            UseCase(custodyStore, inspectionStore, planStore)
                .ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public void PlanningHasNoPathToThePstEngineOrAnyOtherWriteCapableDependency()
    {
        // Prova ESTRUTURAL de que planejar é read-only: o caso de uso não pode nem chamar a engine, porque
        // não a recebe. Nenhuma dependência declarada é capaz de abrir, escrever ou dividir o PST.
        var parameters = typeof(PlanPstPartitionUseCase).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IPstEngine));
        Assert.All(parameters, parameter => Assert.Contains(parameter.ParameterType.Name, new[]
        {
            nameof(IPstCustodyStore), nameof(IPstInspectionStore), nameof(IPartitionPlanStore),
            nameof(IPartitionPlanner), "IClock",
        }));
    }

    // ---- Duplos de teste (implementam as portas diretamente, sem Infrastructure) ----

    private sealed class TestPartitionPlanner(PartitionPolicy policy) : IPartitionPlanner
    {
        public PartitionPlannerIdentity Identity { get; } = new("TestPlanner", "1.0");

        public PartitionPolicy Policy { get; } = policy;

        public PartitionPlan Plan(
            MigrationArtifact custody,
            PstInspectionRecord? canonicalInspection,
            CorrelationId correlation,
            DateTimeOffset plannedAtUtc) =>
            PartitionPlanning.Plan(custody, canonicalInspection, Policy, Identity, correlation, plannedAtUtc);
    }

    private sealed class FakePstCustodyStore(TenantScope? scope, ArtifactId? artifact, MigrationArtifact? record = null)
        : IPstCustodyStore
    {
        public Task<MigrationArtifact?> FindAsync(TenantScope requested, ArtifactId requestedArtifact, CancellationToken cancellationToken) =>
            Task.FromResult(scope is { } expected
                && expected.Tenant == requested.Tenant
                && expected.Project == requested.Project
                && artifact == requestedArtifact
                ? record
                : null);

        public Task<MigrationArtifact> RegisterAsync(
            TenantId tenant, ProjectId project, PstRelativePath relativePath, Sha256Hash observedHash,
            long observedSizeBytes, CancellationToken cancellationToken) =>
            Task.FromResult(MigrationArtifact.Register(tenant, project, relativePath, observedHash, observedSizeBytes, Now));
    }

    private sealed class FakePstInspectionStore(PstInspectionRecord? canonical = null) : IPstInspectionStore
    {
        public int FindCanonicalCount { get; private set; }

        public List<Sha256Hash> RequestedHashes { get; } = [];

        public Task<PstInspectionRecord?> FindCanonicalAsync(
            TenantScope scope, ArtifactId artifact, Sha256Hash expectedHash, CancellationToken cancellationToken)
        {
            FindCanonicalCount++;
            RequestedHashes.Add(expectedHash);
            return Task.FromResult(canonical is not null && canonical.ExpectedHash == expectedHash ? canonical : null);
        }

        public Task<PstInspectionRecord> SaveAsync(PstInspectionRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Planejar nunca grava inspeções.");
    }

    private sealed class FakePartitionPlanStore(
        PartitionPlan? canonical = null, PartitionPlan? canonicalAfterConflict = null, bool throwOnFirstSave = false)
        : IPartitionPlanStore
    {
        // Um plano pré-existente é devolvido como canônico mesmo com identidade divergente: é justamente o
        // cenário de store "mentirosa" que a Application precisa rejeitar (defesa em profundidade).
        private readonly bool _returnsPreexistingUnconditionally = canonical is not null;
        private PartitionPlan? _stored = canonical;

        public int SaveCount { get; private set; }

        public int FindCanonicalCount { get; private set; }

        public Task<PartitionPlan?> FindCanonicalAsync(
            TenantScope scope, ArtifactId artifact, Sha256Hash planHash, CancellationToken cancellationToken)
        {
            FindCanonicalCount++;
            if (canonicalAfterConflict is not null && SaveCount > 0)
            {
                return Task.FromResult<PartitionPlan?>(canonicalAfterConflict);
            }

            return Task.FromResult(
                _stored is not null && (_returnsPreexistingUnconditionally || _stored.PlanHash == planHash)
                    ? _stored
                    : null);
        }

        public Task<PartitionPlan?> FindByIdAsync(TenantScope scope, PartitionPlanId id, CancellationToken cancellationToken) =>
            Task.FromResult(_stored is not null && _stored.Id == id ? _stored : null);

        public Task<PartitionPlan> SaveAsync(PartitionPlan plan, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (throwOnFirstSave && SaveCount == 1)
            {
                throw new PartitionPlanConflictException("corrida simulada");
            }

            if (plan.IsCanonical)
            {
                _stored = plan;
            }

            return Task.FromResult(plan);
        }
    }
}
