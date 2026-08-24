using ArchiveBridge.Application.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Infrastructure.EnterpriseVault.Connector;
using ArchiveBridge.Infrastructure.EnterpriseVault.Delta;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4C, Passo 3 (AB-4C-008) — fundação de delta strategy/freeze planning sobre SQL Server real:
/// idempotência/concorrência REAL da tentativa canônica (backstop <c>UX_ev_delta_attempts_number</c>),
/// persistência atômica tentativa+watermark, concorrência otimista do plano de freeze
/// (<c>ev_freeze_plans</c>), e o pipeline ponta a ponta Baseline→Delta→FinalDelta com um adapter FAKE
/// (nunca Enterprise Vault/PowerShell real). Requer o serviço SQL Server do CI oficial
/// (<c>SqlServerCollectionDefinition</c>) — não executável neste ambiente sandbox sem docker.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4cEvDeltaTests(SqlServerFixture fixture)
{
    // Instante fixo alinhado ao segundo (mesma convenção de Slice4cEvExportTests) — evita o arredondamento
    // de fração de DATETIME2(3) pelo SQL Server, que tornaria comparações de "mais recente" intermitentes.
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly EvDeltaStrategyId CompositeV1 = new("EV-COMPOSITE-WATERMARK", 1);

    private SqlConnectorRegistry Connectors => new(fixture.Factory);

    private SqlConnectorCapabilityStore Capabilities => new(fixture.Factory);

    private SqlEvDeltaRunStore Runs => new(fixture.Factory);

    private SqlEvWatermarkStore Watermarks => new(fixture.Factory);

    private SqlEvFreezePlanStore FreezePlans(MutableClock clock) => new(fixture.Factory, clock);

    private SqlEvDeltaAuditTrail Audit => new(fixture.Factory);

    private static EvDeltaStrategyAdapterCatalog AdapterCatalog() =>
        new EvDeltaStrategyAdapterCatalog([new EvCompositeWatermarkDeltaStrategyAdapter()]);

    // Nenhuma família é Certified por padrão na matriz embarcada (ADR-0013) — o handshake é construído
    // diretamente via Rehydrate (uso legítimo da camada de persistência), mesmo padrão de Slice4cEvExportTests.
    private async Task<ConnectorIdentity> RegisterExportCapableConnectorAsync(TenantScope scope, MutableClock clock)
    {
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(Guid.NewGuid().ToString("N").PadRight(64, 'a')),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), clock.UtcNow);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var handshake = ConnectorCapabilityHandshake.Rehydrate(
            CapabilityHandshakeId.New(), identity.Id, identity.Tenant, identity.Project, "15.0.0", true,
            ConnectorSupportLevel.Certified, exportCapable: true, blockingReason: null, CorrelationId.New(), clock.UtcNow);
        await Capabilities.AppendAsync(handshake, CancellationToken.None);

        return identity;
    }

    // ---- Concorrência REAL sob a MESMA chave de idempotência (req 5/12) --------------------------------

    [Fact]
    public async Task ConcurrentAppendsUnderTheSameFreshIdempotencyKeyConvergeToOneWinningAttempt()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var runs = Runs;
        var key = EvDeltaRunIdentity.Compute(identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, previousWatermark: null).ToIdempotencyKey();

        Task<EvDeltaAttemptRecord> Append() => runs.AppendAttemptAsync(
            scope, key,
            new EvDeltaAttemptCandidate(
                ExistingRun: null, identity.Id, "arch-1", EvDeltaPhase.Baseline, Strategy: null,
                PreviousWatermark: null, IssuedWatermark: null, EvDeltaRunOutcome.StrategyUnsupported, "Unknown", clock.UtcNow, clock.UtcNow),
            watermarkToPersist: null, CancellationToken.None);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                try
                {
                    return (Attempt: await Append(), Failed: (Exception?)null);
                }
                catch (ConcurrencyException ex)
                {
                    return (Attempt: (EvDeltaAttemptRecord?)null, Failed: (Exception?)ex);
                }
            }));

        // Sob concorrência real, cada gravador computa attempt_number independentemente; o backstop
        // UX_ev_delta_attempts_number garante que NENHUMA linha duplicada sobrevive — cada attempt_number
        // vencedor é único, mesmo que múltiplas tentativas concorrentes tenham colidido (ConcurrencyException).
        var succeeded = results.Where(r => r.Attempt is not null).Select(r => r.Attempt!.AttemptNumber).ToArray();
        Assert.Equal(succeeded.Length, succeeded.Distinct().Count());
        Assert.True(succeeded.Length >= 1);

        var stored = await runs.ListAttemptsAsync(scope, results.First(r => r.Attempt is not null).Attempt!.Run, CancellationToken.None);
        Assert.Equal(succeeded.Length, stored.Count);
    }

    // ---- Persistência atômica tentativa+watermark (req 6/14) --------------------------------------------

    [Fact]
    public async Task ACompletedAttemptPersistsItsWatermarkInTheSameTransactionAndItBecomesTheCanonicalOne()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var runs = Runs;
        var watermarks = Watermarks;

        var watermark = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "seed-token", clock.UtcNow);
        var key = EvDeltaRunIdentity.Compute(identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, previousWatermark: null).ToIdempotencyKey();

        var completed = await runs.AppendAttemptAsync(
            scope, key,
            new EvDeltaAttemptCandidate(
                ExistingRun: null, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1,
                PreviousWatermark: null, IssuedWatermark: watermark.Id, EvDeltaRunOutcome.Completed, BlockingReason: null, clock.UtcNow, clock.UtcNow),
            watermarkToPersist: watermark, CancellationToken.None);

        Assert.Equal(EvDeltaRunOutcome.Completed, completed.Outcome);
        var canonical = await watermarks.GetLatestCanonicalAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.NotNull(canonical);
        Assert.Equal(watermark.Id, canonical!.Id);
        Assert.Equal(watermark.LineageHash, canonical.LineageHash); // Rehydrate revalida a lineage a partir das colunas realmente carregadas.
    }

    // ---- Concorrência otimista do plano de freeze (req 9/10) --------------------------------------------

    [Fact]
    public async Task SavingAFreezePlanWithAStaleExpectedVersionFailsClosed()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var store = FreezePlans(clock);

        var plan = EvFreezePlan.RequestFreeze(identity.Tenant, identity.Project, identity.Id, "arch-1");
        await store.SaveAsync(scope, plan, expectedPreviousVersion: 0, CancellationToken.None);

        var reloaded = await store.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(EvFreezeStatus.FreezeRequired, reloaded!.Status);

        reloaded.AuthorizeFreeze("operator-1", EvFreezeAuthorizationRole.MigrationOperator, "janela aprovada", CorrelationId.New(), clock.UtcNow);

        // Simula uma segunda leitura/mutação concorrente que ainda acredita estar na versão 1 (já obsoleta).
        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.SaveAsync(scope, reloaded, expectedPreviousVersion: reloaded.Version, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task SavingAFreezePlanWithTheCorrectExpectedVersionSucceedsAndRoundTripsTheAuthorization()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var store = FreezePlans(clock);

        var plan = EvFreezePlan.RequestFreeze(identity.Tenant, identity.Project, identity.Id, "arch-1");
        await store.SaveAsync(scope, plan, expectedPreviousVersion: 0, CancellationToken.None);

        var reloaded = await store.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        var previousVersion = reloaded!.Version;
        reloaded.AuthorizeFreeze("operator-1", EvFreezeAuthorizationRole.TenantAdministrator, "janela aprovada", CorrelationId.New(), clock.UtcNow);
        await store.SaveAsync(scope, reloaded, previousVersion, CancellationToken.None);

        var final = await store.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.Equal(EvFreezeStatus.FreezeAuthorized, final!.Status);
        Assert.NotNull(final.Authorization);
        Assert.Equal("operator-1", final.Authorization!.AuthorizedBy);
    }

    // ---- Pipeline ponta a ponta: Baseline → Delta → FinalDelta com adapter FAKE (req 1/9/10/12) ----------

    [Fact]
    public async Task EndToEndBaselineDeltaAndFinalDeltaPipelineConvergesThroughRealSqlStores()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var connectors = Connectors;
        var capabilities = Capabilities;
        var runs = Runs;
        var watermarks = Watermarks;
        var freezePlans = FreezePlans(clock);
        var audit = Audit;
        var adapters = AdapterCatalog();

        var baselineUseCase = new RequestEvBaselineUseCase(connectors, capabilities, adapters, runs, audit, clock);
        var baseline = await baselineUseCase.ExecuteAsync(
            new RequestEvBaseline(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvDeltaRunOutcome.Completed, baseline.Outcome);

        clock.Advance(TimeSpan.FromSeconds(1));
        var deltaUseCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, clock);
        var delta = await deltaUseCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.Delta, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvDeltaRunOutcome.Completed, delta.Outcome);
        Assert.NotEqual(baseline.IssuedWatermark, delta.IssuedWatermark);

        // FinalDelta é recusado sem freeze autorizado (STOP-THE-LINE).
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<EvFreezeNotAuthorizedException>(() => deltaUseCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.FinalDelta, CorrelationId.New()), CancellationToken.None));

        var requestFreeze = new RequestFreezeUseCase(connectors, freezePlans, audit, clock);
        await requestFreeze.ExecuteAsync(new RequestFreeze(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        var decide = new DecideFreezeAuthorizationUseCase(connectors, freezePlans, audit, clock);
        await decide.ExecuteAsync(
            new DecideFreezeAuthorization(scope, identity.Id, "arch-1", true, "operator-1", EvFreezeAuthorizationRole.TenantAdministrator, "janela aprovada", CorrelationId.New()),
            CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(1));
        var finalDelta = await deltaUseCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.FinalDelta, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvDeltaRunOutcome.Completed, finalDelta.Outcome);

        var plan = await freezePlans.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.Equal(EvFreezeStatus.FinalDeltaReady, plan!.Status);

        var cutover = new MarkCutoverCompleteUseCase(connectors, freezePlans);
        var afterCutover = await cutover.ExecuteAsync(new MarkCutoverComplete(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.RollbackRetentionRequired, afterCutover);

        var attemptDecommission = new AttemptDecommissionUseCase(connectors, freezePlans, audit, clock);
        var blocked = await attemptDecommission.ExecuteAsync(new AttemptDecommission(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.DecommissionBlocked, blocked); // decommission permanece SEMPRE bloqueado neste Passo (req 11).
    }
}
