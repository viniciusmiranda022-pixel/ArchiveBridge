using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
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
/// incompatível.
/// </summary>
public sealed class WavePartitionOutputBindingUseCaseTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddSeconds(5);
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly PartitionExecutorIdentity Executor = new("TestExecutor", "1.0");

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static PartitionExecutionRecord NewExecution(TenantId tenant, ProjectId project, PartitionPlanId plan, PartitionPlanPartId part)
    {
        var planHash = Hash("plan");
        var sourceHash = Hash("source-bytes");
        return PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), tenant, project, ArtifactId.New(), plan, part, planHash, 1,
            PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096, Executor,
            CorrelationId.New(), StartedAt, CompletedAt);
    }

    private static MigrationWave NewWave(TenantScope scope) =>
        MigrationWave.Create(
            WaveId.New(), scope.Tenant, scope.Project, new WaveName("Onda"),
            TargetRootFolder.ForWave(Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N")[..8]),
            Hash("config"), new WaveSelection([]), Now);

    private static CreateWavePartitionOutputBindingUseCase UseCase(
        FakeWaveStore waves, FakePartitionExecutionStore executions, FakeWavePartitionOutputBindingStore bindings) =>
        new(waves, executions, bindings, new FixedClock(Now));

    [Fact]
    public async Task ExecuteCreatesTheBindingReidratingFromTheCanonicalWaveAndExecution()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(scope);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        var result = await UseCase(waves, executions, bindings)
            .ExecuteAsync(new CreateWavePartitionOutputBindingRequest(scope, wave.Id, plan, part, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(execution.Id, result.Execution);
        Assert.Equal(1, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheWaveDoesNotExistInScope()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore(); // vazio: onda nunca foi semeada.
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(scope, WaveId.New(), plan, part, CorrelationId.New()),
                CancellationToken.None));

        Assert.Equal(0, bindings.SaveCount);
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheWaveBelongsToAnotherTenantOrProject()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var otherScope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(otherScope); // onda existe, mas em OUTRO tenant/projeto.
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(scope, wave.Id, plan, part, CorrelationId.New()),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteFailsClosedWhenTheExecutionIsNotCanonicalForThisPlanAndPart()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(scope);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(canonical: null); // nenhuma execução concluída ainda.
        var bindings = new FakeWavePartitionOutputBindingStore();

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase(waves, executions, bindings).ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(scope, wave.Id, plan, part, CorrelationId.New()),
                CancellationToken.None));

        Assert.Equal(0, bindings.SaveCount);
    }

    [Fact]
    public async Task ARepeatedRequestForTheSameWavePlanAndPartConvergesToTheExistingCanonicalBindingWithoutDuplicating()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(scope);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore();
        var useCase = UseCase(waves, executions, bindings);

        var request = new CreateWavePartitionOutputBindingRequest(scope, wave.Id, plan, part, CorrelationId.New());
        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(request with { Correlation = CorrelationId.New() }, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, bindings.SaveCount); // a segunda chamada NUNCA grava uma linha nova.
    }

    [Fact]
    public async Task ARemappingAttemptToAnIncompatibleOutputForTheSameWavePlanAndPartFailsClosedWithoutOverwriting()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(scope);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var firstExecution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(firstExecution);
        var bindings = new FakeWavePartitionOutputBindingStore();
        var request = new CreateWavePartitionOutputBindingRequest(scope, wave.Id, plan, part, CorrelationId.New());
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
    public async Task ExecuteConvergesAfterLosingAConcurrentCreationRace()
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var wave = NewWave(scope);
        var plan = PartitionPlanId.New();
        var part = PartitionPlanPartId.New();
        var execution = NewExecution(scope.Tenant, scope.Project, plan, part);

        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var executions = new FakePartitionExecutionStore(execution);
        var bindings = new FakeWavePartitionOutputBindingStore(throwConflictOnFirstSave: true);

        var result = await UseCase(waves, executions, bindings).ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, plan, part, CorrelationId.New()), CancellationToken.None);

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
