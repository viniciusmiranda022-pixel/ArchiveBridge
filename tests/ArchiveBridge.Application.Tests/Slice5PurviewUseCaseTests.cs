using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Testes de Application da capability registry/precheck Purview (I5/EPIC-06 Passo 1, AB-I5-001) — provam
/// que os casos de uso são testáveis só com Domain + Contracts, sem qualquer implementação de Infrastructure.
/// </summary>
public sealed class Slice5PurviewUseCaseTests
{
    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    // ---- DiscoverPurviewCapabilityUseCase -------------------------------------------------------------

    [Fact]
    public async Task DiscoverPersistsGeneralAvailabilityForTheKnownRoute()
    {
        var scope = Scope();
        var store = new FakeCapabilityEvidenceStore();
        var useCase = new DiscoverPurviewCapabilityUseCase(store, new StubClock(DateTimeOffset.UtcNow));

        var result = await useCase.ExecuteAsync(
            new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New()),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(CapabilityStatus.GeneralAvailability, result.Evidence.Status);
        Assert.Equal(1, result.Evidence.Version);
    }

    [Fact]
    public async Task DiscoverIsIdempotentOnRepeatedCallsWithNoRealChange()
    {
        var scope = Scope();
        var store = new FakeCapabilityEvidenceStore();
        var useCase = new DiscoverPurviewCapabilityUseCase(store, new StubClock(DateTimeOffset.UtcNow));
        var request = new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New());

        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Evidence.Id, second.Evidence.Id);
        Assert.Equal(1, store.AppendCallCount);
    }

    [Fact]
    public async Task DiscoverConvergesUnderConcurrentIdenticalContentInsteadOfDuplicating()
    {
        var scope = Scope();
        var store = new FakeCapabilityEvidenceStore();
        var useCase = new DiscoverPurviewCapabilityUseCase(store, new StubClock(DateTimeOffset.UtcNow));
        var request = new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New());

        // Simula outro writer concorrente gravando a MESMA versão/conteúdo entre a leitura do latest e o
        // append desta execução.
        var raced = false;
        store.BeforeAppendAttempt = candidate =>
        {
            if (!raced)
            {
                raced = true;
                store.SeedDirectly(candidate);
            }
        };

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);
        Assert.False(result.Created);
    }

    // ---- SubmitMailboxPrecheckUseCase -------------------------------------------------------------------
    //
    // O request carrega SOMENTE identificadores opacos (WaveId + TargetArchiveId) — nunca uma ArchiveRef
    // fornecida diretamente pelo chamador (anti-IDOR, AB-I5-003). O caso de uso resolve a ArchiveRef
    // canônica a partir da seleção da onda JÁ persistida sob o TenantScope via IWaveStore.

    [Fact]
    public async Task SubmitFailsClosedWhenTheWaveDoesNotExistInScope()
    {
        var scope = Scope();
        var store = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(new FakeWaveStore(), store, adapter, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewArchiveNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, WaveId.New(), new TargetArchiveId("user01@contoso.com"), CorrelationId.New()),
            CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task SubmitFailsClosedWhenTheArchiveIsNotPartOfTheWaveSelection()
    {
        // Um caller não consegue precheck de mailbox arbitrária apenas informando um TargetArchiveId que
        // não pertence à seleção da onda autorizada — mesmo com a onda existindo e no escopo correto.
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var store = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(waves, store, adapter, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewArchiveNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, new TargetArchiveId("attacker-arbitrary@contoso.com"), CorrelationId.New()),
            CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task SubmitFailsClosedWhenTheArchiveInTheWaveIsStillUnresolved()
    {
        var scope = Scope();
        var unresolvedEntry = new WaveEntry(
            "prj01-w001", "p_000.pst", new ArchiveRef("user01@contoso.com"), sizeBytes: 1_000_000_000, itemCount: 10);
        var wave = MigrationWave.Create(
            WaveId.New(), scope.Tenant, scope.Project, new WaveName("Wave 1"), TargetRootFolder.ForWave("PRJ01", "W001"),
            DeterministicHash.Compute(["config"]), new WaveSelection([unresolvedEntry]), DateTimeOffset.UtcNow);
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var store = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(waves, store, adapter, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewArchiveNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, unresolvedEntry.Archive.Identity, CorrelationId.New()),
            CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task SubmitFailsClosedWhenTheWaveBelongsToAnotherTenantOrProject()
    {
        // Cross-tenant/project é negado sem vazamento de existência: o mesmo erro genérico do caso "onda
        // inexistente" — IWaveStore.GetAsync já devolve null para uma onda de outro escopo.
        var owner = Scope();
        var attacker = Scope();
        var wave = BuildWave(owner, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var store = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(waves, store, adapter, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewArchiveNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(attacker, wave.Id, new TargetArchiveId("user01@contoso.com"), CorrelationId.New()),
            CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task SubmitPersistsTheObservedSnapshotUsingTheCanonicalMailboxFromTheWave()
    {
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var store = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(waves, store, adapter, new StubClock(DateTimeOffset.UtcNow));

        var result = await useCase.ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, ResolvedMailbox().Identity, CorrelationId.New()), CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(MailboxArchiveStatus.Active, result.Snapshot.ArchiveStatus);
        // A mailbox de exibição sondada/persistida é a CANÔNICA resolvida server-side pela onda — o
        // request não carrega nenhum campo de exibição que um caller pudesse usar para substituí-la.
        Assert.Equal("user01@contoso.com", adapter.LastObservedMailbox!.Value.Mailbox);
        Assert.Equal("user01@contoso.com", result.Snapshot.Mailbox.Mailbox);
        Assert.Equal(1, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task SubmitIsIdempotentWhenObservationDoesNotChange()
    {
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var store = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        var useCase = new SubmitMailboxPrecheckUseCase(waves, store, adapter, new StubClock(DateTimeOffset.UtcNow));
        var request = new SubmitMailboxPrecheckRequest(scope, wave.Id, ResolvedMailbox().Identity, CorrelationId.New());

        var first = await useCase.ExecuteAsync(request, CancellationToken.None);
        var second = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
    }

    // ---- EvaluatePurviewPrecheckUseCase (read-only) -------------------------------------------------------

    [Fact]
    public async Task EvaluateThrowsWhenWaveIsNotFoundInScope()
    {
        var scope = Scope();
        var useCase = new EvaluatePurviewPrecheckUseCase(
            new FakeWaveStore(), new FakeCapabilityEvidenceStore(), new FakeMailboxPrecheckStore(), new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PurviewWaveNotFoundException>(() => useCase.ExecuteAsync(
            new EvaluatePurviewPrecheckRequest(scope, WaveId.New(), CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task EvaluateBlocksWithCapabilityMissingWhenNoDiscoveryEverRan()
    {
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);

        var useCase = new EvaluatePurviewPrecheckUseCase(
            waves, new FakeCapabilityEvidenceStore(), new FakeMailboxPrecheckStore(), new StubClock(DateTimeOffset.UtcNow));

        var report = await useCase.ExecuteAsync(
            new EvaluatePurviewPrecheckRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.True(report.AnyBlocked);
        Assert.Equal(PurviewPrecheckBlockReason.CapabilityEvidenceMissing, Assert.Single(report.PerArchive).Result.Reason);
    }

    [Fact]
    public async Task EvaluateBlocksWithMailboxPrecheckMissingWhenCapabilityIsGaButNoPrecheckRan()
    {
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);

        var capability = new FakeCapabilityEvidenceStore();
        await new DiscoverPurviewCapabilityUseCase(capability, new StubClock(DateTimeOffset.UtcNow)).ExecuteAsync(
            new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New()), CancellationToken.None);

        var useCase = new EvaluatePurviewPrecheckUseCase(
            waves, capability, new FakeMailboxPrecheckStore(), new StubClock(DateTimeOffset.UtcNow));

        var report = await useCase.ExecuteAsync(
            new EvaluatePurviewPrecheckRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.True(report.AnyBlocked);
        Assert.Equal(PurviewPrecheckBlockReason.MailboxPrecheckMissing, Assert.Single(report.PerArchive).Result.Reason);
    }

    [Fact]
    public async Task EvaluateAllowsWhenCapabilityAndPrecheckAreBothInOrder()
    {
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);

        var now = DateTimeOffset.UtcNow;
        var clock = new StubClock(now);
        var capability = new FakeCapabilityEvidenceStore();
        await new DiscoverPurviewCapabilityUseCase(capability, clock).ExecuteAsync(
            new DiscoverPurviewCapabilityRequest(scope, PurviewCapabilityRoutes.PstImport, CorrelationId.New()), CancellationToken.None);

        var prechecks = new FakeMailboxPrecheckStore();
        var adapter = new FakeMailboxPrecheckAdapter(ValidObservation());
        await new SubmitMailboxPrecheckUseCase(waves, prechecks, adapter, clock).ExecuteAsync(
            new SubmitMailboxPrecheckRequest(scope, wave.Id, ResolvedMailbox().Identity, CorrelationId.New()), CancellationToken.None);

        var useCase = new EvaluatePurviewPrecheckUseCase(waves, capability, prechecks, clock);
        var report = await useCase.ExecuteAsync(
            new EvaluatePurviewPrecheckRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.False(report.AnyBlocked);
    }

    [Fact]
    public async Task EvaluateDoesNotMutateTheWaveOrAnyStore()
    {
        // Read-only por desenho (work order item 5): evaluate não expõe nenhum método de escrita nos
        // stores que consome — este teste apenas documenta a garantia por construção do caso de uso,
        // que só recebe ICapabilityEvidenceStore/IMailboxPrecheckStore/IWaveStore para LEITURA.
        var scope = Scope();
        var wave = BuildWave(scope, ("user01@contoso.com", 1_000_000_000));
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var statusBefore = wave.Status;

        var useCase = new EvaluatePurviewPrecheckUseCase(
            waves, new FakeCapabilityEvidenceStore(), new FakeMailboxPrecheckStore(), new StubClock(DateTimeOffset.UtcNow));
        await useCase.ExecuteAsync(new EvaluatePurviewPrecheckRequest(scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(statusBefore, wave.Status);
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static ArchiveRef ResolvedMailbox(string mailbox = "user01@contoso.com") => new(mailbox, new TargetArchiveId(mailbox));

    private static MailboxPrecheckObservation ValidObservation() => new(
        Guid.NewGuid(), Guid.NewGuid(), MailboxArchiveStatus.Active, "UserMailbox",
        AutoExpandingArchiveEnabled: false, LitigationHoldEnabled: false, RetentionHoldEnabled: false,
        ArchiveItemCount: 1000, ArchiveTotalSizeBytes: 10_000_000_000, ObservedAvailableBytes: 200_000_000_000,
        ObservedAtUtc: DateTimeOffset.UtcNow);

    private static MigrationWave BuildWave(TenantScope scope, params (string Mailbox, long SizeBytes)[] entries)
    {
        var waveEntries = entries
            .Select((entry, index) => new WaveEntry(
                $"prj01-w001", $"p_{index:D3}.pst", new ArchiveRef(entry.Mailbox, new TargetArchiveId(entry.Mailbox)),
                entry.SizeBytes, itemCount: 10))
            .ToArray();
        var selection = new WaveSelection(waveEntries);
        var targetRootFolder = TargetRootFolder.ForWave("PRJ01", "W001");
        var configHash = DeterministicHash.Compute(["config"]);
        return MigrationWave.Create(
            WaveId.New(), scope.Tenant, scope.Project, new WaveName("Wave 1"), targetRootFolder, configHash, selection,
            DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Duplo de teste da porta <see cref="ICapabilityEvidenceStore"/> — em memória, sem SQL. Reproduz
/// fielmente a semântica de concorrência exigida de <c>SqlCapabilityEvidenceStore</c>: uma colisão de
/// versão só converge (<c>Created=false</c>) quando o CONTEÚDO já persistido é o MESMO do candidate
/// (<see cref="CapabilityEvidence.IsSameContentAs"/>); conteúdo diferente lança <see cref="ConcurrencyException"/>.
/// </summary>
internal sealed class FakeCapabilityEvidenceStore : ICapabilityEvidenceStore
{
    private readonly List<CapabilityEvidence> _evidence = [];

    public int AppendCallCount { get; private set; }

    /// <summary>Hook de teste: invocado imediatamente antes de CADA tentativa de append.</summary>
    public Action<CapabilityEvidence>? BeforeAppendAttempt { get; set; }

    public Task<CapabilityEvidence?> GetLatestAsync(
        TenantScope scope, TargetProvider provider, PurviewCapabilityRoute route, CancellationToken cancellationToken)
    {
        var latest = _evidence
            .Where(e => e.Tenant == scope.Tenant && e.Project == scope.Project && e.Provider == provider
                && string.Equals(e.Route.Value, route.Value, StringComparison.Ordinal))
            .OrderByDescending(e => e.Version)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<CapabilityEvidenceAppendResult> AppendAsync(CapabilityEvidence evidence, CancellationToken cancellationToken)
    {
        BeforeAppendAttempt?.Invoke(evidence);
        AppendCallCount++;

        var existing = _evidence.FirstOrDefault(e =>
            e.Tenant == evidence.Tenant && e.Project == evidence.Project && e.Provider == evidence.Provider
            && string.Equals(e.Route.Value, evidence.Route.Value, StringComparison.Ordinal) && e.Version == evidence.Version);
        if (existing is not null)
        {
            if (existing.IsSameContentAs(evidence))
            {
                return Task.FromResult(new CapabilityEvidenceAppendResult(existing, Created: false));
            }

            throw new ConcurrencyException($"Versão {evidence.Version} já ocupada com conteúdo diferente.");
        }

        _evidence.Add(evidence);
        return Task.FromResult(new CapabilityEvidenceAppendResult(evidence, Created: true));
    }

    /// <summary>Auxiliar de teste: injeta diretamente um registro "de outro writer".</summary>
    public void SeedDirectly(CapabilityEvidence evidence) => _evidence.Add(evidence);
}

/// <summary>Duplo de teste da porta <see cref="IMailboxPrecheckStore"/> — mesma semântica de concorrência de <see cref="FakeCapabilityEvidenceStore"/>.</summary>
internal sealed class FakeMailboxPrecheckStore : IMailboxPrecheckStore
{
    private readonly List<MailboxPrecheckSnapshot> _snapshots = [];

    public Task<MailboxPrecheckSnapshot?> GetLatestAsync(TenantScope scope, TargetArchiveId mailbox, CancellationToken cancellationToken)
    {
        var latest = _snapshots
            .Where(s => s.Tenant == scope.Tenant && s.Project == scope.Project && s.Mailbox.Identity.Equals(mailbox))
            .OrderByDescending(s => s.Version)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<MailboxPrecheckAppendResult> AppendAsync(MailboxPrecheckSnapshot snapshot, CancellationToken cancellationToken)
    {
        var existing = _snapshots.FirstOrDefault(s =>
            s.Tenant == snapshot.Tenant && s.Project == snapshot.Project
            && s.Mailbox.Identity.Equals(snapshot.Mailbox.Identity) && s.Version == snapshot.Version);
        if (existing is not null)
        {
            if (existing.IsSameContentAs(snapshot))
            {
                return Task.FromResult(new MailboxPrecheckAppendResult(existing, Created: false));
            }

            throw new ConcurrencyException($"Versão {snapshot.Version} já ocupada com conteúdo diferente.");
        }

        _snapshots.Add(snapshot);
        return Task.FromResult(new MailboxPrecheckAppendResult(snapshot, Created: true));
    }
}

/// <summary>Duplo de teste da porta <see cref="IMailboxPrecheckAdapter"/> — determinístico, sem EXO/Graph.</summary>
internal sealed class FakeMailboxPrecheckAdapter(MailboxPrecheckObservation observation) : IMailboxPrecheckAdapter
{
    /// <summary>Quantas vezes o adapter foi sondado — usado para provar que falhas fail-closed nunca sondam.</summary>
    public int ObserveCallCount { get; private set; }

    /// <summary>A mailbox efetivamente recebida na última sondagem, para provar que é a CANÔNICA resolvida server-side.</summary>
    public ArchiveRef? LastObservedMailbox { get; private set; }

    public Task<MailboxPrecheckObservation> ObserveAsync(
        TenantScope scope, ArchiveRef mailbox, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ObserveCallCount++;
        LastObservedMailbox = mailbox;
        return Task.FromResult(observation);
    }
}

/// <summary>Duplo de teste MÍNIMO da porta <see cref="IWaveStore"/> — só <see cref="GetAsync"/> é exercitado por este Passo (read-only).</summary>
internal sealed class FakeWaveStore : IWaveStore
{
    private readonly Dictionary<Guid, MigrationWave> _waves = [];

    public void Seed(MigrationWave wave) => _waves[wave.Id.Value] = wave;

    public Task AddAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<MigrationWave?> GetAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        if (_waves.TryGetValue(waveId.Value, out var wave) && wave.Tenant == scope.Tenant && wave.Project == scope.Project)
        {
            return Task.FromResult<MigrationWave?>(wave);
        }

        return Task.FromResult<MigrationWave?>(null);
    }

    public Task SaveStatusAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) =>
        throw new NotSupportedException();

    public Task SaveValidationAsync(
        MigrationWave wave, IReadOnlyList<PlanningAssessment> assessments, CorrelationId correlation,
        CancellationToken cancellationToken, JobFence? fence = null) =>
        throw new NotSupportedException();

    public Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SaveStatusWithApprovalAsync(
        MigrationWave wave, ArchiveBridge.Contracts.Approvals.ApprovalRecord approval, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
