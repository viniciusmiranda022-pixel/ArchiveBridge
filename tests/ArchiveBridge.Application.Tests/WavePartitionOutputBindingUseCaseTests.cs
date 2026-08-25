using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// AB-I5-010 — <see cref="CreateWavePartitionOutputBindingUseCase"/> testável só com Domain + Contracts,
/// sem qualquer implementação de Infrastructure. Prova o anti-IDOR (onda/execução inexistente ou de outro
/// escopo produzem o MESMO erro), a convergência idempotente e o fail-closed sobre remapeamento
/// incompatível. AB-I5-013 — prova também que a entrada é sempre RESOLVIDA contra a seleção corrente
/// (entrada não-membro produz o MESMO erro anti-IDOR) e que reassignar o mesmo artefato físico a uma
/// entrada diferente dentro da mesma onda é recusado fail-closed.
/// </summary>
public sealed class WavePartitionOutputBindingUseCaseTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly PartitionExecutorIdentity Executor = new("TestExecutor", "1.0");

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static PartitionExecutionRecord NewExecution(
        TenantId tenant, ProjectId project, PartitionPlanId plan, PartitionPlanPartId part, ArtifactId? artifact = null, int sequence = 1)
    {
        var planHash = Hash("plan-" + plan.Value);
        var sourceHash = Hash("source-bytes-" + plan.Value + "-" + sequence);
        return PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), tenant, project, artifact ?? ArtifactId.New(), plan, part, planHash, sequence,
            PartitionPlanIdentity.ComputePartKey(planHash, sequence), sourceHash, 4096, sourceHash, 4096, Executor,
            CorrelationId.New(), StartedAt, CompletedAt);
    }

    private static WaveEntry NewWaveEntry(string pstName = "mailbox-a.pst", string mailbox = "mailbox-a@contoso.com") =>
        new($"C:\\pst\\{pstName}", pstName, new ArchiveRef(mailbox), 4096, 10);

    private static MigrationWave NewWave(TenantScope scope, params WaveEntry[] entries) =>
        MigrationWave.Create(
            WaveId.New(), scope.Tenant, scope.Project, new WaveName("Onda"),
            TargetRootFolder.ForWave(Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N")[..8]),
            Hash("config"), new WaveSelection(entries), Now);

    private static CreateWavePartitionOutputBindingUseCase UseCase(
        FakeWaveStore waves, FakePartitionExecutionStore executions, FakeWavePartitionOutputBindingStore bindings) =>
        new(waves, executions, bindings, new FixedClock(Now));

    [Fact]
    public async Task ExecuteCreatesTheBindingReidratingFromTheCanonicalWaveAndExecution()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(scope, entry);
        var entryId = WaveEntryId.Derive(wave.Id, entry);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        var result = await UseCase(waves, executions, bindings)
            .ExecuteAsync(new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, plan, part, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(execution.Id, result.Execution);
        Assert.Equal(entryId, result.Entry);
        Assert.Equal(1, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheWaveDoesNotExistInScope()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);
        var unknownWave = WaveId.New();

        var waves = new FakeWaveStore(); // vazio: onda nunca foi semeada.
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scope, unknownWave, WaveEntryId.Derive(unknownWave, NewWaveEntry()), plan, part, CorrelationId.New()),
                CancellationToken.None));

        Assert.Equal(0, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheWaveBelongsToAnotherTenantOrProject()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var otherScope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(otherScope, entry); // onda existe, mas em OUTRO tenant/projeto.
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), plan, part, CorrelationId.New()),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheEntryIsNotAMemberOfTheWaveSCurrentSelection()
    {
        // AB-I5-013 item 3: um ID de entrada que não corresponde a NENHUMA entrada da seleção corrente
        // (ex.: calculado para outra onda, ou para uma entrada que nunca existiu) é recusado com o MESMO
        // erro anti-IDOR de onda/execução inexistente — nunca revela se a onda existe mas a entrada não.
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(scope, NewWaveEntry());
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);
        var foreignEntryId = WaveEntryId.Derive(wave.Id, NewWaveEntry("not-a-member.pst", "not-a-member@contoso.com"));

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(scope, wave.Id, foreignEntryId, plan, part, CorrelationId.New()),
                CancellationToken.None));

        Assert.Equal(0, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheExecutionIsNotCanonicalForThisPlanAndPart()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(scope, entry);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(canonical: null); // nenhuma execução concluída ainda.
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), plan, part, CorrelationId.New()),
                CancellationToken.None));

        Assert.Equal(0, bindings.SaveCount);
    }

    [Fact]
    public async Task ARepeatedRequestForTheSameWavePlanAndPartConvergesToTheExistingCanonicalBindingWithoutDuplicating()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(scope, entry);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();
        var useCase = UseCase(waves, executions, bindings);

        var request = new CreateWavePartitionOutputBindingRequest(
            scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), plan, part, CorrelationId.New());
        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(request with { Correlation = CorrelationId.New() }, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, bindings.SaveCount); // a segunda chamada NUNCA grava uma linha nova.
    }

    [Fact]
    public async Task ARemappingAttemptToAnIncompatibleOutputForTheSameWavePlanAndPartFailsClosedWithoutOverwriting()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(scope, entry);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var firstExecution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(firstExecution);
        var bindings = new FakeWavePartitionOutputBindingStore();
        var entryId = WaveEntryId.Derive(wave.Id, entry);
        var request = new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, plan, part, CorrelationId.New());
        var original = await UseCase(waves, executions, bindings).ExecuteAsync(request, CancellationToken.None);

        // Uma SEGUNDA execução canônica "aparece" para o mesmo plano/parte (cenário anômalo) — o vínculo já
        // persistido nunca é silenciosamente substituído.
        var secondExecution = NewExecution(scope.Tenant, scope.Project, plan, part);
        var executionsWithDifferentCanonical = new FakePartitionExecutionStore(secondExecution);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIncompatibleException>(() =>
            UseCase(waves, executionsWithDifferentCanonical, bindings).ExecuteAsync(
                request with { Correlation = CorrelationId.New() }, CancellationToken.None));

        Assert.Equal(1, bindings.SaveCount);
        Assert.Equal(original.Execution, firstExecution.Id); // evidência original preservada, nunca sobrescrita.
    }

    [Fact]
    public async Task ARequestForTheSamePlanAndPartWithADifferentEntryFailsClosedWithoutOverwriting()
    {
        // AB-I5-013 item 4/5: mesmo (wave, plano, parte) já vinculado, mas a SEGUNDA chamada informa uma
        // entrada de destino diferente — nunca converge, mesmo que o output físico seja idêntico.
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var firstEntry = NewWaveEntry("first.pst", "first@contoso.com");
        var secondEntry = NewWaveEntry("second.pst", "second@contoso.com");
        var wave = NewWave(scope, firstEntry, secondEntry);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();
        var useCase = UseCase(waves, executions, bindings);

        var request = new CreateWavePartitionOutputBindingRequest(
            scope, wave.Id, WaveEntryId.Derive(wave.Id, firstEntry), plan, part, CorrelationId.New());
        await useCase.ExecuteAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIncompatibleException>(() =>
            useCase.ExecuteAsync(
                request with { Entry = WaveEntryId.Derive(wave.Id, secondEntry), Correlation = CorrelationId.New() },
                CancellationToken.None));

        Assert.Equal(1, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheSamePhysicalArtifactIsAlreadyCanonicallyBoundToADifferentEntryInTheSameWave()
    {
        // AB-I5-013 item 4: o MESMO artefato físico (replanejado sob um NOVO plano/parte) não pode ser
        // reatribuído silenciosamente a uma entrada de destino diferente da já vinculada nesta onda.
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var firstEntry = NewWaveEntry("first.pst", "first@contoso.com");
        var secondEntry = NewWaveEntry("second.pst", "second@contoso.com");
        var wave = NewWave(scope, firstEntry, secondEntry);
        var artifact = ArtifactId.New();

        var firstPlan = PartitionPlanId.New();
        var firstPart = PartitionPlanPartId.New();
        var firstExecution = NewExecution(scope.Tenant, scope.Project, firstPlan, firstPart, artifact);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var bindings = new FakeWavePartitionOutputBindingStore();
        await UseCase(waves, new FakePartitionExecutionStore(firstExecution), bindings).ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, firstEntry), firstPlan, firstPart, CorrelationId.New()),
            CancellationToken.None);

        // O MESMO artefato reaparece sob um plano/parte DIFERENTE (replanejamento), agora pedindo a
        // SEGUNDA entrada como destino.
        var secondPlan = PartitionPlanId.New();
        var secondPart = PartitionPlanPartId.New();
        var secondExecution = NewExecution(scope.Tenant, scope.Project, secondPlan, secondPart, artifact);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIncompatibleException>(() =>
            UseCase(waves, new FakePartitionExecutionStore(secondExecution), bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scope, wave.Id, WaveEntryId.Derive(wave.Id, secondEntry), secondPlan, secondPart, CorrelationId.New()),
                CancellationToken.None));

        Assert.Equal(1, bindings.SaveCount);
    }

    [Fact]
    public async Task ASecondPhysicalPartOfTheSameOversizedPstMayBindToTheSameEntryAsTheFirstPart()
    {
        // Caso legítimo (item 7 "multiple PST parts for one mailbox"): um PST grande particionado em
        // várias partes físicas distintas — todas apontando para a MESMA entrada de destino — nunca é
        // tratado como reassignação ambígua.
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(scope, entry);
        var entryId = WaveEntryId.Derive(wave.Id, entry);

        var firstPlan = PartitionPlanId.New();
        var firstPart = PartitionPlanPartId.New();
        var firstExecution = NewExecution(scope.Tenant, scope.Project, firstPlan, firstPart, sequence: 1);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var bindings = new FakeWavePartitionOutputBindingStore();
        await UseCase(waves, new FakePartitionExecutionStore(firstExecution), bindings).ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, firstPlan, firstPart, CorrelationId.New()),
            CancellationToken.None);

        var secondPlan = PartitionPlanId.New();
        var secondPart = PartitionPlanPartId.New();
        var secondExecution = NewExecution(scope.Tenant, scope.Project, secondPlan, secondPart, sequence: 2);

        var second = await UseCase(waves, new FakePartitionExecutionStore(secondExecution), bindings).ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, secondPlan, secondPart, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(entryId, second.Entry);
        Assert.Equal(2, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteConvergesAfterLosingAConcurrentCreationRace()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var entry = NewWaveEntry();
        var wave = NewWave(scope, entry);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore(throwConflictOnFirstSave: true);

        var result = await UseCase(waves, executions, bindings).ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), plan, part, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(execution.Id, result.Execution);
    }

    // ---- Duplos de teste (Domain + Contracts apenas — sem Infrastructure) ----

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakePartitionExecutionStore(PartitionExecutionRecord? canonical) : IPartitionExecutionStore
    {
        public Task<PartitionExecutionRecord?> FindCanonicalAsync(
            TenantScope scope, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken) =>
            Task.FromResult(
                canonical is not null && canonical.Plan == plan && canonical.Part == part
                    && canonical.Tenant == scope.Tenant && canonical.Project == scope.Project
                    ? canonical
                    : null);

        public Task<PartitionExecutionRecord> SaveAsync(PartitionExecutionRecord execution, CancellationToken cancellationToken) =>
            throw new NotSupportedException("O caso de uso de vínculo nunca grava execuções.");
    }

    private sealed class FakeWaveStore : IWaveStore
    {
        private readonly Dictionary<(Guid Tenant, Guid Project, Guid Wave), MigrationWave> _waves = [];

        public void Seed(MigrationWave wave) => _waves[(wave.Tenant.Value, wave.Project.Value, wave.Id.Value)] = wave;

        public Task<MigrationWave?> GetAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken) =>
            Task.FromResult(_waves.GetValueOrDefault((scope.Tenant.Value, scope.Project.Value, waveId.Value)));

        public Task AddAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException("O caso de uso de vínculo nunca cria ondas.");

        public Task SaveStatusAsync(
            MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) =>
            throw new NotSupportedException("O caso de uso de vínculo nunca transiciona o status da onda.");

        public Task SaveValidationAsync(
            MigrationWave wave,
            IReadOnlyList<ArchiveBridge.Domain.Planning.PlanningAssessment> assessments,
            CorrelationId correlation,
            CancellationToken cancellationToken,
            JobFence? fence = null) =>
            throw new NotSupportedException("O caso de uso de vínculo nunca grava avaliações.");

        public Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException("O caso de uso de vínculo nunca altera a seleção da onda.");

        public Task SaveStatusWithApprovalAsync(
            MigrationWave wave, ArchiveBridge.Contracts.Approvals.ApprovalRecord approval, CancellationToken cancellationToken) =>
            throw new NotSupportedException("O caso de uso de vínculo nunca aprova ondas.");
    }

    private sealed class FakeWavePartitionOutputBindingStore(bool throwConflictOnFirstSave = false) : IWavePartitionOutputBindingStore
    {
        private readonly Dictionary<(Guid Wave, Guid Plan, Guid Part), WavePartitionOutputBinding> _canonical = [];

        public int SaveCount { get; private set; }

        public Task<WavePartitionOutputBinding?> FindCanonicalAsync(
            TenantScope scope, WaveId wave, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken) =>
            Task.FromResult(_canonical.GetValueOrDefault((wave.Value, plan.Value, part.Value)));

        public Task<IReadOnlyList<WavePartitionOutputBinding>> ListForWaveAsync(
            TenantScope scope, WaveId wave, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WavePartitionOutputBinding>>(
                [.. _canonical.Values.Where(binding => binding.Wave == wave)]);

        public Task<WavePartitionOutputBinding> SaveAsync(WavePartitionOutputBinding binding, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (throwConflictOnFirstSave && SaveCount == 1)
            {
                // Simula uma corrida: outra chamada gravou o canônico ANTES desta, sem que este caller o
                // tenha visto na sua própria leitura (a próxima releitura do laço de convergência o encontra).
                _canonical[(binding.Wave.Value, binding.Plan.Value, binding.Part.Value)] = binding;
                throw new WavePartitionOutputBindingConflictException("Corrida simulada.");
            }

            _canonical[(binding.Wave.Value, binding.Plan.Value, binding.Part.Value)] = binding;
            return Task.FromResult(binding);
        }
    }
}
