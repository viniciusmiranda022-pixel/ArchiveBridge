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
/// Slice 4B, Passo 1 — o caso de uso de inspeção resolve toda identidade/custódia SERVER-SIDE, nunca
/// reinvoca a engine para um resultado canônico já persistido (idempotência), falha fechado em
/// divergência de hash (staleness) e em limite excedido, e nunca deixa uma corrida de gravação virar erro
/// de negócio (relê o canônico). Duplos de teste implementam as portas diretamente — sem Infrastructure.
/// </summary>
public sealed class InspectPstArtifactUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    private static TenantScope NewScope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static MigrationArtifact Custody(TenantScope scope, Sha256Hash hash) =>
        MigrationArtifact.Register(scope.Tenant, scope.Project, new PstRelativePath("a.pst"), hash, 100, Now);

    [Fact]
    public async Task NonexistentArtifactFailsClosedWithoutEngineOrSave()
    {
        var scope = NewScope();
        var custodyStore = new FakePstCustodyStore(scope: null, artifact: null); // nunca resolve
        var inspectionStore = new FakePstInspectionStore();
        var engine = new FakePstEngine(new PstInspectionResult(Hash("x"), 1, PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(scope, ArtifactId.New(), CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, engine.InvocationCount);
        Assert.Equal(0, inspectionStore.SaveCount);
    }

    [Fact]
    public async Task CrossTenantAndCrossProjectAreIndistinguishableFromNotFound()
    {
        var ownerScope = NewScope();
        var hash = Hash("owned");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(ownerScope, artifact, Custody(ownerScope, hash));
        var inspectionStore = new FakePstInspectionStore();
        var engine = new FakePstEngine(new PstInspectionResult(hash, 100, PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var crossProjectScope = new TenantScope(ownerScope.Tenant, new ProjectId(Guid.NewGuid()));
        var crossTenantScope = new TenantScope(new TenantId(Guid.NewGuid()), ownerScope.Project);

        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(crossProjectScope, artifact, CorrelationId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PstArtifactNotFoundException>(() =>
            useCase.ExecuteAsync(crossTenantScope, artifact, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, engine.InvocationCount);
        Assert.Equal(0, inspectionStore.SaveCount);
    }

    [Fact]
    public async Task ExistingCanonicalIsReplayedWithoutInvokingEngineAgain()
    {
        var scope = NewScope();
        var hash = Hash("stable");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var canonical = PstInspectionRecord.Complete(
            InspectionId.New(), scope.Tenant, scope.Project, artifact, hash, hash, 100,
            PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, "Engine", "1.0", CorrelationId.New(), Now, Now);
        var inspectionStore = new FakePstInspectionStore(canonical);
        var engine = new FakePstEngine(new PstInspectionResult(hash, 100, PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Same(canonical, result);
        Assert.Equal(0, engine.InvocationCount); // "sem duplicar efeitos"
        Assert.Equal(0, inspectionStore.SaveCount);
    }

    [Fact]
    public async Task MatchingObservedHashPersistsCompletedCanonicalRecord()
    {
        var scope = NewScope();
        var hash = Hash("match");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var inspectionStore = new FakePstInspectionStore();
        var engine = new FakePstEngine(new PstInspectionResult(hash, 100, PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Completed, result.Outcome);
        Assert.True(result.IsCanonical);
        Assert.Equal(1, engine.InvocationCount);
        Assert.Equal(1, inspectionStore.SaveCount);
    }

    [Fact]
    public async Task DivergentObservedHashFailsClosedAsStaleNotCanonical()
    {
        var scope = NewScope();
        var registeredHash = Hash("registered");
        var driftedHash = Hash("drifted");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, registeredHash));
        var inspectionStore = new FakePstInspectionStore();
        var engine = new FakePstEngine(new PstInspectionResult(driftedHash, 100, PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Stale, result.Outcome);
        Assert.False(result.IsCanonical);
        Assert.Equal(registeredHash, result.ExpectedHash);
        Assert.Equal(driftedHash, result.ObservedHash);
    }

    [Fact]
    public async Task ReadErrorProducesCompletedRecordNotStale()
    {
        // Sem hash confiável (leitura não concluída) — nunca deve virar Stale, que exigiria comparação
        // de hash; é um diagnóstico ESTRUTURAL definitivo (nunca crash, nunca sucesso falso).
        var scope = NewScope();
        var registeredHash = Hash("registered");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, registeredHash));
        var inspectionStore = new FakePstInspectionStore();
        var engine = new FakePstEngine(new PstInspectionResult(null, null, PstStructuralDiagnostic.ReadError, PstFormatVariant.Unknown, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.Completed, result.Outcome);
        Assert.Equal(PstStructuralDiagnostic.ReadError, result.Diagnostic);
        Assert.Null(result.ObservedHash);
        Assert.False(result.IsCanonical);
    }

    [Fact]
    public async Task LimitExceededByEngineIsPersistedFailClosedNeverAsSuccess()
    {
        var scope = NewScope();
        var registeredHash = Hash("registered");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, registeredHash));
        var inspectionStore = new FakePstInspectionStore();
        var engine = new FakePstEngine(exception: new PstInspectionLimitExceededException("MAX_SIZE_EXCEEDED"));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PstInspectionOutcome.LimitExceeded, result.Outcome);
        Assert.False(result.IsCanonical);
        Assert.Null(result.ObservedHash);
    }

    [Fact]
    public async Task ConcurrentSaveConflictRereadsCanonicalInsteadOfFailing()
    {
        // Corrida: outra execução concorrente venceu o índice único filtrado no meio-tempo. A store
        // lança PstInspectionConflictException no SaveAsync; a Application relê e devolve o canônico já
        // persistido em vez de propagar o conflito como erro de negócio.
        var scope = NewScope();
        var hash = Hash("race");
        var artifact = ArtifactId.New();
        var custodyStore = new FakePstCustodyStore(scope, artifact, Custody(scope, hash));
        var winner = PstInspectionRecord.Complete(
            InspectionId.New(), scope.Tenant, scope.Project, artifact, hash, hash, 100,
            PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, "Other", "1.0", CorrelationId.New(), Now, Now);
        var inspectionStore = new FakePstInspectionStore(canonicalAfterConflict: winner, throwOnFirstSave: true);
        var engine = new FakePstEngine(new PstInspectionResult(hash, 100, PstStructuralDiagnostic.Valid, PstFormatVariant.Unicode2013Plus, null, null));
        var useCase = new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, new StubClock(Now));

        var result = await useCase.ExecuteAsync(scope, artifact, CorrelationId.New(), CancellationToken.None);

        Assert.Same(winner, result);
        Assert.Equal(1, inspectionStore.SaveCount); // tentou gravar uma vez, conflitou, não tentou de novo
    }

    // ---- Duplos de teste (implementam as portas diretamente, sem Infrastructure) ----

    private sealed class FakePstCustodyStore : IPstCustodyStore
    {
        private readonly TenantScope? _scope;
        private readonly ArtifactId? _artifact;
        private readonly MigrationArtifact? _record;

        public FakePstCustodyStore(TenantScope? scope, ArtifactId? artifact, MigrationArtifact? record = null)
        {
            _scope = scope;
            _artifact = artifact;
            _record = record;
        }

        public Task<MigrationArtifact?> FindAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken) =>
            Task.FromResult(_scope is { } expectedScope
                && expectedScope.Tenant == scope.Tenant
                && expectedScope.Project == scope.Project
                && _artifact == artifact
                ? _record
                : null);

        public Task<MigrationArtifact> RegisterAsync(
            TenantId tenant, ProjectId project, PstRelativePath relativePath, Sha256Hash observedHash,
            long observedSizeBytes, CancellationToken cancellationToken) =>
            Task.FromResult(MigrationArtifact.Register(tenant, project, relativePath, observedHash, observedSizeBytes, Now));
    }

    private sealed class FakePstInspectionStore : IPstInspectionStore
    {
        private readonly PstInspectionRecord? _canonical;
        private readonly PstInspectionRecord? _canonicalAfterConflict;
        private readonly bool _throwOnFirstSave;

        public FakePstInspectionStore(PstInspectionRecord? canonical = null, PstInspectionRecord? canonicalAfterConflict = null, bool throwOnFirstSave = false)
        {
            _canonical = canonical;
            _canonicalAfterConflict = canonicalAfterConflict;
            _throwOnFirstSave = throwOnFirstSave;
        }

        public int SaveCount { get; private set; }

        public int FindCanonicalCount { get; private set; }

        public Task<PstInspectionRecord?> FindCanonicalAsync(
            TenantScope scope, ArtifactId artifact, Sha256Hash expectedHash, CancellationToken cancellationToken)
        {
            FindCanonicalCount++;
            var value = FindCanonicalCount > 1 ? _canonicalAfterConflict ?? _canonical : _canonical;
            return Task.FromResult(value);
        }

        public Task<PstInspectionRecord> SaveAsync(PstInspectionRecord record, CancellationToken cancellationToken)
        {
            SaveCount++;
            if (_throwOnFirstSave && SaveCount == 1)
            {
                throw new PstInspectionConflictException("corrida simulada");
            }

            return Task.FromResult(record);
        }
    }

    private sealed class FakePstEngine : IPstEngine
    {
        private readonly PstInspectionResult? _result;
        private readonly Exception? _exception;

        public FakePstEngine(PstInspectionResult result)
        {
            _result = result;
        }

        public FakePstEngine(Exception exception)
        {
            _exception = exception;
        }

        public int InvocationCount { get; private set; }

        public string EngineName => "FakeEngine";

        public string EngineVersion => "0.0";

        public Task<PstInspectionResult> InspectAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return _exception is not null
                ? Task.FromException<PstInspectionResult>(_exception)
                : Task.FromResult(_result!);
        }
    }
}
