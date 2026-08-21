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
/// Slice 4B, Passo 3 — o caso de uso de execução só materializa planos canônicos
/// <see cref="PartitionPlanReason.SinglePartWithinTarget"/>, revalida a origem contra a custódia atual
/// (fail-closed sobre staleness) ANTES de qualquer I/O, reaproveita idempotentemente uma execução canônica
/// já persistida, resolve corrida de gravação relendo o canônico, e nunca confia cegamente no que a store
/// devolve como canônico. Duplos de teste implementam as portas diretamente — sem Infrastructure.
/// </summary>
public sealed class ExecutePartitionPlanUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly PartitionPlannerIdentity Planner = new("TestPlanner", "1.0");
    private static readonly PartitionPolicy Policy = PartitionPolicy.Create(4096, 8192);

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static MigrationArtifact Custody(TenantScope scope, Sha256Hash hash, long size = 2048) =>
        MigrationArtifact.Register(scope.Tenant, scope.Project, new PstRelativePath("a.pst"), hash, size, Now);

    private static PstInspectionRecord Canonical(TenantScope scope, ArtifactId artifact, Sha256Hash hash, long size) =>
        PstInspectionRecord.Complete(
            InspectionId.New(), scope.Tenant, scope.Project, artifact, hash, hash, size,
            PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, "Engine", "1.0",
            CorrelationId.New(), Now, Now);

    private static PartitionPlan PlannedSinglePart(MigrationArtifact custody, PstInspectionRecord inspection) =>
        PartitionPlanning.Plan(custody, inspection, Policy, Planner, CorrelationId.New(), Now);

    private static PartitionPlan UnsupportedPlan(MigrationArtifact custody, PstInspectionRecord inspection)
    {
        // Origem excede o alvo da política ⇒ Unsupported/ItemInventoryUnavailable (zero partes).
        var oversizedInspection = PstInspectionRecord.Complete(
            InspectionId.New(), custody.Tenant, custody.Project, custody.Id, custody.RegisteredHash,
            custody.RegisteredHash, Policy.TargetPartBytes + 1, PstStructuralDiagnostic.Valid,
            PstFormatVariant.Unicode2013Plus, "Engine", "1.0", CorrelationId.New(), Now, Now);
        return PartitionPlanning.Plan(custody, oversizedInspection, Policy, Planner, CorrelationId.New(), Now);
    }

    private static ExecutePartitionPlanUseCase UseCase(
        FakePstCustodyStore custodyStore,
        FakePartitionPlanStore planStore,
        FakePartitionExecutionStore executionStore,
        FakePartitionPartWriter? writer = null,
        FakePartitionPartVerifier? verifier = null) =>
        new(custodyStore, planStore, executionStore, writer ?? new FakePartitionPartWriter(), verifier ?? new FakePartitionPartVerifier(), new StubClock(Now));

    // ---- Elegibilidade: só SinglePartWithinTarget canônico executa ----

    [Fact]
    public async Task NonexistentPlanFailsClosedWithoutTouchingCustodyOrWriter()
    {
        var scope = NewScope();
        var custodyStore = new FakePstCustodyStore(scope: null, artifact: null);
        var planStore = new FakePartitionPlanStore(plan: null);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter();

        await Assert.ThrowsAsync<PartitionPlanNotFoundException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer)
                .ExecuteAsync(scope, PartitionPlanId.New(), CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount);
        Assert.Equal(0, executionStore.SaveCount);
    }

    [Fact]
    public async Task CrossTenantAndCrossProjectPlanLookupIsIndistinguishableFromNotFound()
    {
        var ownerScope = NewScope();
        var hash = Hash("owned");
        var custody = Custody(ownerScope, hash);
        var inspection = Canonical(ownerScope, custody.Id, hash, 1024);
        var plan = PlannedSinglePart(custody, inspection);

        var custodyStore = new FakePstCustodyStore(ownerScope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter();
        var useCase = UseCase(custodyStore, planStore, executionStore, writer);

        await Assert.ThrowsAsync<PartitionPlanNotFoundException>(() => useCase.ExecuteAsync(
            new TenantScope(ownerScope.Tenant, new ProjectId(Guid.NewGuid())), plan.Id, CorrelationId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PartitionPlanNotFoundException>(() => useCase.ExecuteAsync(
            new TenantScope(new TenantId(Guid.NewGuid()), ownerScope.Project), plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount);
    }

    [Fact]
    public async Task UnsupportedPlanIsRejectedWithoutAnyExternalEffect()
    {
        var scope = NewScope();
        var hash = Hash("big");
        var custody = Custody(scope, hash, size: Policy.TargetPartBytes + 1);
        var inspection = Canonical(scope, custody.Id, hash, Policy.TargetPartBytes + 1);
        var plan = UnsupportedPlan(custody, inspection);
        Assert.Equal(PartitionPlanOutcome.Unsupported, plan.Outcome); // pré-condição do teste

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter();

        await Assert.ThrowsAsync<PartitionExecutionNotEligibleException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount);
        Assert.Equal(0, executionStore.SaveCount);
    }

    [Fact]
    public async Task ForgedPlanIdentityIsRejectedBeforeAnyIO()
    {
        var scope = NewScope();
        var hash = Hash("forged");
        var custody = Custody(scope, hash);
        var inspection = Canonical(scope, custody.Id, hash, 1024);
        var source = new PartitionPlanSource(custody.Id, inspection.Id, hash, 1024, "Engine", "1.0");

        // planHash arbitrário (não recalculado) ⇒ HasConsistentIdentity() falso. PartitionPlan.Create não
        // revalida a identidade (só Rehydrate faz isso) — é exatamente o "plano com identidade forjada" que
        // a Application precisa recusar por conta própria.
        var forgedPlanHash = Hash("not-the-real-hash");
        var part = new PartitionPlanPart(
            PartitionPlanPartId.New(), 1, PartitionPlanIdentity.ComputePartKey(forgedPlanHash, 1), 1024, coversEntireSource: true);
        var plan = PartitionPlan.Create(
            PartitionPlanId.New(), scope.Tenant, scope.Project, source, Policy, Planner, forgedPlanHash,
            PartitionPlanReason.SinglePartWithinTarget, [part], CorrelationId.New(), Now);
        Assert.False(plan.HasConsistentIdentity()); // pré-condição do teste

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter();

        await Assert.ThrowsAsync<PartitionExecutionNotEligibleException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount);
    }

    // ---- Staleness: a origem tem de ser a MESMA que o plano descreve ----

    [Fact]
    public async Task SourceChangedSincePlanningFailsClosedWithoutTouchingTheWriter()
    {
        var scope = NewScope();
        var plannedHash = Hash("planned-content");
        var custodyAtPlanning = Custody(scope, plannedHash);
        var inspection = Canonical(scope, custodyAtPlanning.Id, plannedHash, 2048);
        var plan = PlannedSinglePart(custodyAtPlanning, inspection);

        // Custódia ATUAL diverge do hash gravado no plano (artefato mudou desde o planejamento).
        var changedCustody = MigrationArtifact.Register(
            scope.Tenant, scope.Project, new PstRelativePath("a.pst"), Hash("changed-content"), 2048, Now);
        var custodyStore = new FakePstCustodyStore(scope, custodyAtPlanning.Id, changedCustody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter();

        await Assert.ThrowsAsync<PartitionExecutionSourceStaleException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount);
        Assert.Equal(0, executionStore.SaveCount);
    }

    // ---- Caminho feliz ----

    [Fact]
    public async Task EligiblePlanIsExecutedAndPersistedAsCanonical()
    {
        var scope = NewScope();
        var hash = Hash("happy");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var expectedCorrelation = CorrelationId.New();
        var expectedCompletedAtUtc = Now.AddSeconds(1);
        var writer = new FakePartitionPartWriter(
            new PartitionPartArtifact(hash, 2048, hash, expectedCorrelation, Now, expectedCompletedAtUtc));

        var result = await UseCase(custodyStore, planStore, executionStore, writer)
            .ExecuteAsync(scope, plan.Id, expectedCorrelation, CancellationToken.None);

        Assert.Equal(1, writer.ExecuteCount);
        Assert.Equal(1, executionStore.SaveCount);
        Assert.Equal(hash, result.OutputHash);
        Assert.Equal(2048, result.OutputSizeBytes);
        Assert.Equal(plan.Id, result.Plan);
        Assert.Equal(plan.Parts[0].Id, result.Part);
        Assert.True(result.HasConsistentIdentity());

        // Lineage do checkpoint SQL vem do que o writer devolveu (lido do manifesto físico já validado) —
        // nunca recalculada localmente pela Application (work order AB-4B-006 item 7 / correção AB-4B-008).
        Assert.Equal(hash, result.SourceHash);
        Assert.Equal(expectedCorrelation, result.Correlation);
        Assert.Equal(Now, result.StartedAtUtc);
        Assert.Equal(expectedCompletedAtUtc, result.CompletedAtUtc);

        // O contexto fornecido ao writer é determinado pela Application ANTES da materialização: correlação
        // do chamador e início vindo do relógio injetado — nunca inventado pelo writer.
        Assert.Equal(expectedCorrelation, writer.LastContext?.Correlation);
        Assert.Equal(Now, writer.LastContext?.StartedAtUtc);
    }

    // ---- Idempotência / concorrência ----

    [Fact]
    public async Task ExistingCanonicalExecutionIsReplayedWithoutInvokingTheWriter()
    {
        var scope = NewScope();
        var hash = Hash("replay");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);
        var part = plan.Parts[0];

        var existing = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, custody.Id, plan.Id, part.Id, plan.PlanHash,
            part.Sequence, part.PartKey, hash, 2048, hash, 2048, new PartitionExecutorIdentity("Executor", "1.0"),
            CorrelationId.New(), Now, Now);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore(existing);
        var writer = new FakePartitionPartWriter();
        var verifier = new FakePartitionPartVerifier();

        var result = await UseCase(custodyStore, planStore, executionStore, writer, verifier)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(0, writer.ExecuteCount); // réplay: nenhum I/O de ESCRITA reexecutado.
        Assert.Equal(0, executionStore.SaveCount);
        Assert.Equal(1, verifier.VerifyCount); // mas o bundle físico É revalidado (somente leitura) antes do réplay.
        Assert.Equal(existing.Id, verifier.LastVerified?.Id);
    }

    [Fact]
    public async Task TamperedPersistedOutputFailsClosedOnReplayWithoutTheWriterEverRunning()
    {
        var scope = NewScope();
        var hash = Hash("tampered-replay");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);
        var part = plan.Parts[0];

        var existing = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, custody.Id, plan.Id, part.Id, plan.PlanHash,
            part.Sequence, part.PartKey, hash, 2048, hash, 2048, new PartitionExecutorIdentity("Executor", "1.0"),
            CorrelationId.New(), Now, Now);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore(existing);
        var writer = new FakePartitionPartWriter();
        // Simula um bundle físico removido/adulterado depois que o checkpoint SQL já existia.
        var verifier = new FakePartitionPartVerifier(throwsTampered: true);

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer, verifier)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount); // fail-closed: nunca tenta reconstruir o output.
        Assert.Equal(0, executionStore.SaveCount);
        Assert.Equal(1, verifier.VerifyCount);
    }

    [Fact]
    public async Task ConcurrentWriteConflictReReadsTheCanonicalResultInsteadOfFailing()
    {
        var scope = NewScope();
        var hash = Hash("concurrent");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);
        var part = plan.Parts[0];

        var racedWinner = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, custody.Id, plan.Id, part.Id, plan.PlanHash,
            part.Sequence, part.PartKey, hash, 2048, hash, 2048, new PartitionExecutorIdentity("Executor", "1.0"),
            CorrelationId.New(), Now, Now);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore(canonical: null, canonicalAfterConflict: racedWinner, throwOnFirstSave: true);
        var writer = new FakePartitionPartWriter(new PartitionPartArtifact(hash, 2048, hash, CorrelationId.New(), Now, Now));
        var verifier = new FakePartitionPartVerifier();

        var result = await UseCase(custodyStore, planStore, executionStore, writer, verifier)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(racedWinner.Id, result.Id);
        Assert.Equal(1, writer.ExecuteCount); // ainda copiou (idempotente no filesystem) — perdeu só a corrida do INSERT.
        Assert.Equal(1, verifier.VerifyCount); // o vencedor da corrida também é revalidado antes de ser devolvido.
        Assert.Equal(racedWinner.Id, verifier.LastVerified?.Id);
    }

    [Fact]
    public async Task ConflictWithoutACanonicalRowOnRereadIsAnUnresolvedInvariantViolation()
    {
        var scope = NewScope();
        var hash = Hash("unresolved");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore(canonical: null, canonicalAfterConflict: null, throwOnFirstSave: true);
        var writer = new FakePartitionPartWriter(new PartitionPartArtifact(hash, 2048, hash, CorrelationId.New(), Now, Now));

        await Assert.ThrowsAsync<PartitionExecutionConflictUnresolvedException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task AnExecutionStoreLyingAboutCanonicityIsRejectedRatherThanReused()
    {
        var scope = NewScope();
        var hash = Hash("lying-store");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);
        var part = plan.Parts[0];

        // Execução "canônica" cuja part_key não corresponde ao plano-alvo (planHash diferente).
        var otherPlanHash = Hash("some-other-plan");
        var inconsistent = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, custody.Id, plan.Id, part.Id, otherPlanHash,
            part.Sequence, PartitionPlanIdentity.ComputePartKey(otherPlanHash, part.Sequence), hash, 2048, hash,
            2048, new PartitionExecutorIdentity("Executor", "1.0"), CorrelationId.New(), Now, Now);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore(inconsistent);
        var writer = new FakePartitionPartWriter();

        await Assert.ThrowsAsync<PartitionExecutionCanonicityViolationException>(() =>
            UseCase(custodyStore, planStore, executionStore, writer)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, writer.ExecuteCount);
    }

    // ---- Lineage (AB-4B-008): contexto fornecido ANTES da materialização, nunca reinventado depois ----

    [Fact]
    public async Task TheWriterReceivesADeterministicContextComputedBeforeMaterializationStarts()
    {
        var scope = NewScope();
        var hash = Hash("context-before-write");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter();
        var correlation = CorrelationId.New();

        await UseCase(custodyStore, planStore, executionStore, writer)
            .ExecuteAsync(scope, plan.Id, correlation, CancellationToken.None);

        // O contexto (correlação/início) é o MESMO fornecido pela Application ANTES de qualquer I/O — nunca
        // um valor inventado pelo writer depois do fato (work order AB-4B-006 item 7 / correção AB-4B-008).
        Assert.NotNull(writer.LastContext);
        Assert.Equal(correlation, writer.LastContext!.Correlation);
        Assert.Equal(Now, writer.LastContext.StartedAtUtc);
    }

    [Fact]
    public async Task LineageOfAConvergedBundleFromAnEarlierExecutionIsPersistedAsReturnedNotRecomputedLocally()
    {
        // Simula o writer convergindo para um bundle publicado por uma execução ANTERIOR/concorrente
        // (crash-recovery/corrida) cuja lineage própria diverge do contexto desta chamada — a Application
        // nunca "corrige" isso com seus próprios valores locais, sempre confia no que o writer devolveu
        // (lido de volta do manifesto físico já revalidado).
        var scope = NewScope();
        var hash = Hash("converged-bundle");
        var custody = Custody(scope, hash, size: 2048);
        var inspection = Canonical(scope, custody.Id, hash, 2048);
        var plan = PlannedSinglePart(custody, inspection);

        var earlierCorrelation = CorrelationId.New();
        var earlierStarted = Now.AddMinutes(-5);
        var earlierCompleted = Now.AddMinutes(-4);
        var earlierExecutionArtifact = new PartitionPartArtifact(hash, 2048, hash, earlierCorrelation, earlierStarted, earlierCompleted);

        var custodyStore = new FakePstCustodyStore(scope, custody.Id, custody);
        var planStore = new FakePartitionPlanStore(plan);
        var executionStore = new FakePartitionExecutionStore();
        var writer = new FakePartitionPartWriter(earlierExecutionArtifact);

        var result = await UseCase(custodyStore, planStore, executionStore, writer)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None); // correlação DESTA chamada, diferente da anterior.

        Assert.Equal(earlierCorrelation, result.Correlation);
        Assert.Equal(earlierStarted, result.StartedAtUtc);
        Assert.Equal(earlierCompleted, result.CompletedAtUtc);
    }

    // ---- Duplos de teste (Domain + Contracts apenas — sem Infrastructure) ----

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

    private sealed class FakePartitionPlanStore(PartitionPlan? plan) : IPartitionPlanStore
    {
        public Task<PartitionPlan?> FindCanonicalAsync(
            TenantScope scope, ArtifactId artifact, Sha256Hash planHash, CancellationToken cancellationToken) =>
            Task.FromResult(plan is not null && plan.PlanHash == planHash ? plan : null);

        public Task<PartitionPlan?> FindByIdAsync(TenantScope scope, PartitionPlanId id, CancellationToken cancellationToken) =>
            Task.FromResult(
                plan is not null && plan.Id == id && plan.Tenant == scope.Tenant && plan.Project == scope.Project
                    ? plan
                    : null);

        public Task<PartitionPlan> SaveAsync(PartitionPlan plan, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Executar nunca grava planos.");
    }

    private sealed class FakePartitionExecutionStore(
        PartitionExecutionRecord? canonical = null,
        PartitionExecutionRecord? canonicalAfterConflict = null,
        bool throwOnFirstSave = false) : IPartitionExecutionStore
    {
        public int SaveCount { get; private set; }

        public Task<PartitionExecutionRecord?> FindCanonicalAsync(
            TenantScope scope, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken)
        {
            if (canonicalAfterConflict is not null && SaveCount > 0)
            {
                return Task.FromResult<PartitionExecutionRecord?>(canonicalAfterConflict);
            }

            return Task.FromResult(canonical is not null && canonical.Plan == plan && canonical.Part == part ? canonical : null);
        }

        public Task<PartitionExecutionRecord> SaveAsync(PartitionExecutionRecord execution, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (throwOnFirstSave && SaveCount == 1)
            {
                throw new PartitionExecutionConflictException("Corrida simulada.");
            }

            return Task.FromResult(execution);
        }
    }

    private sealed class FakePartitionPartWriter(PartitionPartArtifact? result = null) : IPartitionPartWriter
    {
        public int ExecuteCount { get; private set; }

        public PartitionExecutionContext? LastContext { get; private set; }

        public string ExecutorName => "FakeExecutor";

        public string ExecutorVersion => "0.0";

        public Task<PartitionPartArtifact> ExecuteAsync(
            TenantScope scope, MigrationArtifact source, PartitionPlan plan, PartitionPlanPart part,
            PartitionExecutionContext context, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            LastContext = context;

            // Duplo de teste: reflete a lineage que um bundle físico REAL conteria (source hash fixo pelo
            // plano, correlação/início ecoados do contexto fornecido pela Application, conclusão um instante
            // depois) — nunca inventa um valor divergente do que a Application forneceu.
            return Task.FromResult(result ?? new PartitionPartArtifact(
                source.RegisteredHash, source.RegisteredSizeBytes, source.RegisteredHash, context.Correlation,
                context.StartedAtUtc, context.StartedAtUtc));
        }
    }

    /// <summary>
    /// Duplo de teste do revalidador físico (AB-4B-007). Por padrão "confirma" qualquer execução persistida
    /// (simula um bundle íntegro); com <see cref="_throwsTampered"/> simula um bundle removido/adulterado
    /// depois que o checkpoint SQL já existia — exatamente o cenário que a correção precisa recusar fechado.
    /// </summary>
    private sealed class FakePartitionPartVerifier(bool throwsTampered = false) : IPartitionPartVerifier
    {
        private readonly bool _throwsTampered = throwsTampered;

        public int VerifyCount { get; private set; }

        public PartitionExecutionRecord? LastVerified { get; private set; }

        public Task VerifyAsync(TenantScope scope, PartitionExecutionRecord execution, CancellationToken cancellationToken)
        {
            VerifyCount++;
            LastVerified = execution;
            if (_throwsTampered)
            {
                throw new PartitionExecutionOutputTamperedException(
                    "Bundle simulado como removido/adulterado depois do checkpoint SQL (duplo de teste).");
            }

            return Task.CompletedTask;
        }
    }
}
