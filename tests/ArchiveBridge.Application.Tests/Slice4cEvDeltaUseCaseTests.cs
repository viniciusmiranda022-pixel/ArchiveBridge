using ArchiveBridge.Application.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Testes de Application da fundação de delta strategy/freeze planning (Slice 4C, Passo 3, AB-4C-008) —
/// provam que os casos de uso são testáveis só com Domain + Contracts, sem qualquer implementação de
/// Infrastructure/SQL/PowerShell real. Reaproveita <c>FakeConnectorRegistryForExport</c>/
/// <c>FakeConnectorCapabilityStoreForExport</c> já definidos para o Passo 2.
/// </summary>
public sealed class Slice4cEvDeltaUseCaseTests
{
    private static readonly EvDeltaStrategyId CompositeV1 = new("EV-COMPOSITE-WATERMARK", 1);

    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static ConnectorIdentity ActiveConnector(TenantScope scope, DateTimeOffset now) =>
        ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('a', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);

    private static ConnectorCapabilityHandshake ExportCapableHandshake(ConnectorIdentity identity, DateTimeOffset now, string evVersion = "15.0.0") =>
        ConnectorCapabilityHandshake.Rehydrate(
            CapabilityHandshakeId.New(), identity.Id, identity.Tenant, identity.Project, evVersion, true,
            ConnectorSupportLevel.Certified, exportCapable: true, blockingReason: null, CorrelationId.New(), now);

    private static (
        FakeConnectorRegistryForExport Connectors, FakeConnectorCapabilityStoreForExport Capabilities,
        FakeEvDeltaStrategyAdapterCatalog Adapters, FakeEvDeltaRunStore Runs, FakeEvWatermarkStore Watermarks,
        FakeEvFreezePlanStore FreezePlans, FakeEvDeltaAuditTrail Audit) Wiring()
    {
        var watermarks = new FakeEvWatermarkStore();
        return (new FakeConnectorRegistryForExport(), new FakeConnectorCapabilityStoreForExport(), new FakeEvDeltaStrategyAdapterCatalog(),
            new FakeEvDeltaRunStore(watermarks), watermarks, new FakeEvFreezePlanStore(), new FakeEvDeltaAuditTrail());
    }

    // ---- RequestEvBaselineUseCase --------------------------------------------------------------------

    [Fact]
    public async Task BaselineConnectorNotFoundIsFailClosed()
    {
        var scope = Scope();
        var (connectors, capabilities, adapters, runs, _, _, audit) = Wiring();
        var useCase = new RequestEvBaselineUseCase(connectors, capabilities, adapters, runs, audit, new AdvancingStubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ConnectorNotFoundException>(() => useCase.ExecuteAsync(
            new RequestEvBaseline(scope, ConnectorId.New(), "arch-1", CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task BaselineWithUnknownEvVersionBlocksFailClosedAndRecordsTheAttempt()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, _, _, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now, evVersion: "1.0.0")); // família inexistente na matriz

        var useCase = new RequestEvBaselineUseCase(connectors, capabilities, adapters, runs, audit, new AdvancingStubClock(now));

        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => useCase.ExecuteAsync(
            new RequestEvBaseline(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None));

        var recorded = Assert.Single(await runs.ListAllAsync());
        Assert.Equal(EvDeltaRunOutcome.StrategyUnsupported, recorded.Outcome);
        Assert.Null(recorded.Strategy);
        Assert.Contains(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.StrategySelected);
    }

    // AB-4C-009 item 1 (fail-closed): a família "15.0.0" é reconhecida pela matriz embarcada, mas apenas no
    // nível Compatible — NENHUMA família de produção está Certified neste Passo — então baseline/delta/
    // final-delta permanecem bloqueados mesmo com connector ativo, capability exportável e (para FinalDelta)
    // freeze autorizado. O caminho "adapter chamado ⇒ Completed" só é alcançável quando a policy resolve
    // Supported (provado isoladamente com um descriptor Certified injetado em Slice4cEvDeltaDomainTests).

    [Fact]
    public async Task BaselineWithACompatibleOnlyFamilyIsBlockedFailClosedAndIsIdempotentOnRetry()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, _, _, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now)); // evVersion "15.0.0" — família Compatible, nunca Certified.
        var adapter = new FakeEvDeltaStrategyAdapter(CompositeV1);
        adapters.Register(adapter);

        var useCase = new RequestEvBaselineUseCase(connectors, capabilities, adapters, runs, audit, new AdvancingStubClock(now));
        var request = new RequestEvBaseline(scope, identity.Id, "arch-1", CorrelationId.New());

        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => useCase.ExecuteAsync(request, CancellationToken.None));
        var second = await useCase.ExecuteAsync(request, CancellationToken.None); // retry converge, não relança silenciosamente uma 2ª tentativa nova
        Assert.True(second.Replayed);

        var recorded = Assert.Single(await runs.ListAllAsync());
        Assert.Equal(EvDeltaRunOutcome.StrategyUnsupported, recorded.Outcome);
        Assert.Null(recorded.IssuedWatermark);
        Assert.Equal(0, adapter.BaselineCallCount); // NUNCA chamado — Compatible não é elegível para execução canônica
        Assert.Contains(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.StrategySelected);
        Assert.DoesNotContain(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.BaselineCompleted);
        Assert.DoesNotContain(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.WatermarkAccepted);
    }

    // ---- RequestEvDeltaUseCase -----------------------------------------------------------------------

    [Fact]
    public async Task DeltaRejectsBaselinePhase()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, watermarks, freezePlans, audit) = Wiring();
        var useCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, new AdvancingStubClock(now));

        await Assert.ThrowsAsync<EvDeltaValidationException>(() => useCase.ExecuteAsync(
            new RequestEvDelta(scope, ConnectorId.New(), "arch-1", EvDeltaPhase.Baseline, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task DeltaWithoutAPriorBaselineFailsClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, watermarks, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now));

        var useCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, new AdvancingStubClock(now));

        await Assert.ThrowsAsync<EvDeltaValidationException>(() => useCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.Delta, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task DeltaWithACompatibleOnlyFamilyIsBlockedFailClosedEvenWithAValidPreviousWatermark()
    {
        // Diferente do bloqueio "sem baseline anterior" (DeltaWithoutAPriorBaselineFailsClosed), aqui o
        // watermark anterior existe e é da MESMA strategy/escopo — ainda assim a seleção (Compatible, não
        // Certified) bloqueia ANTES de qualquer inspeção de lineage do watermark anterior (EnsureCanPrecede
        // nunca chega a ser chamado; a validação de lineage cross-scope/strategy/downgrade é coberta em
        // Slice4cEvDeltaDomainTests diretamente sobre EvWatermark).
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, watermarks, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now));
        var adapter = new FakeEvDeltaStrategyAdapter(CompositeV1);
        adapters.Register(adapter);
        watermarks.Seed(EvWatermark.Issue(identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "t", now));

        var useCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, new AdvancingStubClock(now.AddSeconds(1)));
        var request = new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.Delta, CorrelationId.New());

        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => useCase.ExecuteAsync(request, CancellationToken.None));
        var second = await useCase.ExecuteAsync(request, CancellationToken.None); // retry converge (terminal já persistido)
        Assert.True(second.Replayed);

        var recorded = Assert.Single(await runs.ListAllAsync());
        Assert.Equal(EvDeltaRunOutcome.StrategyUnsupported, recorded.Outcome);
        Assert.Equal(0, adapter.IncrementalCallCount); // NUNCA chamado
    }

    [Fact]
    public async Task DeltaBlocksAtStrategySelectionBeforeEverInspectingThePreviousWatermarkLineage()
    {
        // Mesmo quando o watermark anterior JÁ seria recusado por outro motivo (EnsureCanPrecede:
        // StrategyMismatch, provado isoladamente em Slice4cEvDeltaDomainTests), a seleção de strategy
        // (Compatible, não Certified) é o primeiro gate fail-closed e bloqueia ANTES — o desfecho persistido
        // é StrategyUnsupported, nunca WatermarkRejected, e o retry converge sem duplicar linha.
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, watermarks, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now));
        var adapter = new FakeEvDeltaStrategyAdapter(CompositeV1);
        adapters.Register(adapter);

        var foreignStrategy = new EvDeltaStrategyId("SOME-OTHER-STRATEGY", 1);
        var foreignWatermark = EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, foreignStrategy, Guid.NewGuid(), "foreign-token", now);
        watermarks.Seed(foreignWatermark);

        var useCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, new AdvancingStubClock(now.AddSeconds(1)));
        var request = new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.Delta, CorrelationId.New());

        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => useCase.ExecuteAsync(request, CancellationToken.None));

        // O retry recomputa a MESMA identidade canônica (nenhum watermark novo foi persistido pela
        // tentativa bloqueada) e encontra a tentativa TERMINAL já registrada — devolvida como replay, sem
        // relançar nem reexecutar a seleção/validação/o adapter.
        var second = await useCase.ExecuteAsync(request, CancellationToken.None);
        Assert.True(second.Replayed);
        Assert.Equal(EvDeltaRunOutcome.StrategyUnsupported, second.Outcome);

        var recorded = await runs.ListAllAsync();
        var blocked = Assert.Single(recorded); // uma ÚNICA linha — o retry converge, nunca duplica
        Assert.Equal(EvDeltaRunOutcome.StrategyUnsupported, blocked.Outcome);
        Assert.Equal(0, adapter.IncrementalCallCount); // NUNCA chamado em nenhuma das duas tentativas
        Assert.Contains(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.StrategySelected);
    }

    [Fact]
    public async Task FinalDeltaWithoutAnAuthorizedFreezeIsRejected()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, watermarks, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now));
        adapters.Register(new FakeEvDeltaStrategyAdapter(CompositeV1));
        watermarks.Seed(EvWatermark.Issue(identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "t", now));

        var useCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, new AdvancingStubClock(now.AddSeconds(1)));

        await Assert.ThrowsAsync<EvFreezeNotAuthorizedException>(() => useCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.FinalDelta, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task FinalDeltaIsBlockedFailClosedByStrategySelectionEvenWithAnAuthorizedFreeze()
    {
        // STOP-THE-LINE (freeze) e certificação (AB-4C-009 item 1) são gates INDEPENDENTES: um freeze
        // autorizado remove o bloqueio de EvFreezeNotAuthorizedException, mas NÃO substitui a exigência de
        // strategy Certified — o plano permanece em FreezeAuthorized (nunca promovido a FinalDeltaReady)
        // porque o delta final nunca chega a Completed.
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, capabilities, adapters, runs, watermarks, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        capabilities.Seed(ExportCapableHandshake(identity, now));
        var adapter = new FakeEvDeltaStrategyAdapter(CompositeV1);
        adapters.Register(adapter);
        watermarks.Seed(EvWatermark.Issue(identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "t", now));

        var plan = EvFreezePlan.RequestFreeze(identity.Tenant, identity.Project, identity.Id, "arch-1");
        plan.AuthorizeFreeze("operator-1", EvFreezeAuthorizationRole.TenantAdministrator, "janela aprovada", CorrelationId.New(), now);
        await freezePlans.SaveAsync(scope, plan, expectedPreviousVersion: 0, CancellationToken.None);

        var useCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, new AdvancingStubClock(now.AddSeconds(1)));

        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => useCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.FinalDelta, CorrelationId.New()), CancellationToken.None));

        var savedPlan = await freezePlans.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.Equal(EvFreezeStatus.FreezeAuthorized, savedPlan!.Status); // NUNCA promovido a FinalDeltaReady sem um delta final Completed
        Assert.Equal(0, adapter.IncrementalCallCount);
        Assert.DoesNotContain(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.FinalDeltaReady);
    }

    // ---- Freeze lifecycle use cases -------------------------------------------------------------------

    [Fact]
    public async Task FullFreezeLifecycleEndsPermanentlyBlockedAtDecommission()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, _, _, _, _, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        var clock = new AdvancingStubClock(now);

        var requestFreeze = new RequestFreezeUseCase(connectors, freezePlans, audit, clock);
        var requested = await requestFreeze.ExecuteAsync(new RequestFreeze(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.True(requested.Created);
        Assert.Equal(EvFreezeStatus.FreezeRequired, requested.Status);

        var decide = new DecideFreezeAuthorizationUseCase(connectors, freezePlans, audit, clock);
        var authorized = await decide.ExecuteAsync(
            new DecideFreezeAuthorization(scope, identity.Id, "arch-1", Approved: true, "operator-1", EvFreezeAuthorizationRole.MigrationOperator, "janela", CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(EvFreezeStatus.FreezeAuthorized, authorized);

        // Simula a conclusão do delta final diretamente no plano (o caminho real é exercido por RequestEvDeltaUseCase acima).
        var plan = (await freezePlans.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None))!;
        var beforeFinalDelta = plan.Version;
        plan.MarkFinalDeltaReady();
        await freezePlans.SaveAsync(scope, plan, beforeFinalDelta, CancellationToken.None);

        var cutover = new MarkCutoverCompleteUseCase(connectors, freezePlans);
        var afterCutover = await cutover.ExecuteAsync(new MarkCutoverComplete(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.RollbackRetentionRequired, afterCutover);

        var attemptDecommission = new AttemptDecommissionUseCase(connectors, freezePlans, audit, clock);
        var blocked = await attemptDecommission.ExecuteAsync(new AttemptDecommission(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.DecommissionBlocked, blocked);

        // Idempotente: chamar de novo não lança e permanece bloqueado.
        var blockedAgain = await attemptDecommission.ExecuteAsync(new AttemptDecommission(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.DecommissionBlocked, blockedAgain);
        Assert.Single(audit.Events, e => e.EventCode == EvDeltaAuditEventCode.DecommissionBlocked);
    }

    [Fact]
    public async Task AuthorizingWithUnspecifiedRoleIsRejectedByTheUseCase()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, _, _, _, _, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        var plan = EvFreezePlan.RequestFreeze(identity.Tenant, identity.Project, identity.Id, "arch-1");
        await freezePlans.SaveAsync(scope, plan, expectedPreviousVersion: 0, CancellationToken.None);

        var decide = new DecideFreezeAuthorizationUseCase(connectors, freezePlans, audit, new AdvancingStubClock(now));

        await Assert.ThrowsAsync<EvFreezeAuthorizationRequiredException>(() => decide.ExecuteAsync(
            new DecideFreezeAuthorization(scope, identity.Id, "arch-1", Approved: true, "operator-1", EvFreezeAuthorizationRole.Unspecified, "justificativa", CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task DecidingOnANonExistentFreezePlanIsFailClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var (connectors, _, _, _, _, freezePlans, audit) = Wiring();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);

        var decide = new DecideFreezeAuthorizationUseCase(connectors, freezePlans, audit, new AdvancingStubClock(now));

        await Assert.ThrowsAsync<EvDeltaNotFoundException>(() => decide.ExecuteAsync(
            new DecideFreezeAuthorization(scope, identity.Id, "arch-1", Approved: true, "operator-1", EvFreezeAuthorizationRole.MigrationOperator, "j", CorrelationId.New()),
            CancellationToken.None));
    }
}

// ---- Duplos de teste (em memória, sem SQL/PowerShell) --------------------------------------------------

/// <summary>Relógio que avança a cada leitura — necessário para provar ordenação estrita de watermarks (Stale, req 13).</summary>
internal sealed class AdvancingStubClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _current = start;

    public DateTimeOffset UtcNow
    {
        get
        {
            var value = _current;
            _current = _current.AddMilliseconds(1);
            return value;
        }
    }
}

internal sealed class FakeEvDeltaStrategyAdapter(EvDeltaStrategyId strategyId) : IEvDeltaStrategyAdapter
{
    public EvDeltaStrategyId StrategyId { get; } = strategyId;

    public int BaselineCallCount { get; private set; }

    public int IncrementalCallCount { get; private set; }

    public bool ThrowOnBaseline { get; set; }

    public bool ThrowOnIncrement { get; set; }

    public Task<EvWatermarkIssueResult> IssueBaselineWatermarkAsync(EvDeltaBaselineIssueRequest request, CancellationToken cancellationToken)
    {
        BaselineCallCount++;
        if (ThrowOnBaseline)
        {
            throw new InvalidOperationException("simulated adapter failure");
        }

        return Task.FromResult(new EvWatermarkIssueResult($"baseline-token-{BaselineCallCount}", request.EvVersionDisplay));
    }

    public Task<EvWatermarkIssueResult> IssueIncrementalWatermarkAsync(EvDeltaIncrementIssueRequest request, CancellationToken cancellationToken)
    {
        IncrementalCallCount++;
        if (ThrowOnIncrement)
        {
            throw new InvalidOperationException("simulated adapter failure");
        }

        return Task.FromResult(new EvWatermarkIssueResult($"delta-token-{IncrementalCallCount}", request.EvVersionDisplay));
    }
}

internal sealed class FakeEvDeltaStrategyAdapterCatalog : IEvDeltaStrategyAdapterCatalog
{
    private readonly Dictionary<EvDeltaStrategyId, IEvDeltaStrategyAdapter> _adapters = [];

    public void Register(IEvDeltaStrategyAdapter adapter) => _adapters[adapter.StrategyId] = adapter;

    public IEvDeltaStrategyAdapter? Resolve(EvDeltaStrategyId strategyId) => _adapters.GetValueOrDefault(strategyId);
}

internal sealed class FakeEvDeltaRunStore(FakeEvWatermarkStore? watermarks = null) : IEvDeltaRunStore
{
    private readonly List<(Guid Key, EvDeltaAttemptRecord Record)> _records = [];
    private readonly FakeEvWatermarkStore? _watermarks = watermarks;

    public Task<IReadOnlyList<EvDeltaAttemptRecord>> ListAllAsync() =>
        Task.FromResult<IReadOnlyList<EvDeltaAttemptRecord>>(_records.Select(r => r.Record).ToList());

    public Task<EvDeltaAttemptRecord?> GetLatestByIdempotencyKeyAsync(TenantScope scope, Guid canonicalIdempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_records.Where(r => r.Key == canonicalIdempotencyKey)
            .OrderByDescending(r => r.Record.AttemptNumber).Select(r => (EvDeltaAttemptRecord?)r.Record).FirstOrDefault());

    public async Task<EvDeltaAttemptRecord> AppendAttemptAsync(
        TenantScope scope, Guid canonicalIdempotencyKey, EvDeltaAttemptCandidate candidate, EvWatermark? watermarkToPersist, CancellationToken cancellationToken)
    {
        var runId = candidate.ExistingRun ?? EvDeltaRunId.New();
        var attemptNumber = _records.Where(r => r.Key == canonicalIdempotencyKey).Select(r => r.Record.AttemptNumber).DefaultIfEmpty(0).Max() + 1;
        var record = new EvDeltaAttemptRecord(
            runId, EvDeltaAttemptId.New(), attemptNumber, candidate.Connector, candidate.ExternalArchiveId, candidate.Phase,
            candidate.Strategy, candidate.PreviousWatermark, candidate.IssuedWatermark, candidate.Outcome, candidate.BlockingReason,
            candidate.StartedAtUtc, candidate.CompletedAtUtc);
        _records.Add((canonicalIdempotencyKey, record));

        // Emula a MESMA transação da implementação SQL real: a tentativa Completed e o watermark são
        // persistidos juntos (ver SqlEvDeltaRunStore/AppendAttemptAsync).
        if (watermarkToPersist is not null && _watermarks is not null)
        {
            await _watermarks.AppendAsync(scope, watermarkToPersist, cancellationToken).ConfigureAwait(false);
        }

        return record;
    }

    public Task<IReadOnlyList<EvDeltaAttemptRecord>> ListAttemptsAsync(TenantScope scope, EvDeltaRunId run, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvDeltaAttemptRecord>>(
            _records.Where(r => r.Record.Run == run).OrderBy(r => r.Record.AttemptNumber).Select(r => r.Record).ToList());
}

internal sealed class FakeEvWatermarkStore : IEvWatermarkStore
{
    private readonly List<EvWatermark> _watermarks = [];

    public void Seed(EvWatermark watermark) => _watermarks.Add(watermark);

    public Task AppendAsync(TenantScope scope, EvWatermark watermark, CancellationToken cancellationToken)
    {
        _watermarks.Add(watermark);
        return Task.CompletedTask;
    }

    public Task<EvWatermark?> GetLatestCanonicalAsync(TenantScope scope, ConnectorId connector, string externalArchiveId, CancellationToken cancellationToken) =>
        Task.FromResult(_watermarks
            .Where(w => w.Connector == connector && w.ExternalArchiveId == externalArchiveId && w.Tenant == scope.Tenant && w.Project == scope.Project)
            .OrderByDescending(w => w.IssuedAtUtc)
            .FirstOrDefault());

    public Task<EvWatermark?> GetByIdAsync(TenantScope scope, WatermarkId id, CancellationToken cancellationToken) =>
        Task.FromResult(_watermarks.FirstOrDefault(w => w.Id == id));
}

internal sealed class FakeEvFreezePlanStore : IEvFreezePlanStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project, Guid Connector, string Archive), EvFreezePlan> _plans = [];

    public Task<EvFreezePlan?> GetAsync(TenantScope scope, ConnectorId connector, string externalArchiveId, CancellationToken cancellationToken)
    {
        var found = _plans.GetValueOrDefault((scope.Tenant.Value, scope.Project.Value, connector.Value, externalArchiveId));
        return Task.FromResult(found is null ? null : Copy(found));
    }

    public Task SaveAsync(TenantScope scope, EvFreezePlan plan, int expectedPreviousVersion, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, plan.Connector.Value, plan.ExternalArchiveId);
        if (_plans.TryGetValue(key, out var existing))
        {
            if (existing.Version != expectedPreviousVersion)
            {
                throw new ConcurrencyException("Versão esperada não corresponde à persistida.");
            }
        }
        else if (expectedPreviousVersion != 0)
        {
            throw new ConcurrencyException("Nenhum plano existente para a versão esperada informada.");
        }

        // Isola o valor persistido do objeto mutável do chamador — mesma semântica de valor de uma linha
        // SQL real (SqlEvFreezePlanStore sempre reidrata uma instância NOVA a partir do banco).
        _plans[key] = Copy(plan);
        return Task.CompletedTask;
    }

    private static EvFreezePlan Copy(EvFreezePlan plan) =>
        EvFreezePlan.Rehydrate(plan.Id, plan.Tenant, plan.Project, plan.Connector, plan.ExternalArchiveId, plan.Status, plan.Authorization, plan.Version);
}

internal sealed class FakeEvDeltaAuditTrail : IEvDeltaAuditTrail
{
    public List<EvDeltaAuditEvent> Events { get; } = [];

    public Task AppendAsync(TenantScope scope, EvDeltaAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
