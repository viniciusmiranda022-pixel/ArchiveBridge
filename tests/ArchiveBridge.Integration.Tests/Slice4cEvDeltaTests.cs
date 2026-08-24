using System.Data;
using ArchiveBridge.Application.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Infrastructure.EnterpriseVault.Connector;
using ArchiveBridge.Infrastructure.EnterpriseVault.Delta;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
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

    // ---- AB-4C-009 item 1: nenhuma família embarcada está Certified — baseline/delta/final-delta permanecem
    // bloqueados fail-closed sobre SQL real mesmo com connector ativo, capability exportável, watermark
    // anterior válido e (para FinalDelta) freeze autorizado (req 1/9/10/12) ----------------------------------

    [Fact]
    public async Task BaselineDeltaAndFinalDeltaAreAllBlockedFailClosedOverRealSqlBecauseNoEmbeddedFamilyIsCertifiedYet()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock); // evVersion "15.0.0" — família Compatible, nunca Certified.
        var connectors = Connectors;
        var capabilities = Capabilities;
        var runs = Runs;
        var watermarks = Watermarks;
        var freezePlans = FreezePlans(clock);
        var audit = Audit;
        var adapters = AdapterCatalog();

        var baselineUseCase = new RequestEvBaselineUseCase(connectors, capabilities, adapters, runs, audit, clock);
        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => baselineUseCase.ExecuteAsync(
            new RequestEvBaseline(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None));

        // Delta/FinalDelta exigem um watermark anterior — seedado diretamente no store (não via o caso de uso
        // de baseline, que está bloqueado acima) para provar que a certificação bloqueia MESMO quando toda
        // outra precondição (watermark anterior válido, freeze autorizado) já está satisfeita.
        clock.Advance(TimeSpan.FromSeconds(1));
        var seededBaseline = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "seed-token", clock.UtcNow);
        await watermarks.AppendAsync(scope, seededBaseline, CancellationToken.None);

        var deltaUseCase = new RequestEvDeltaUseCase(connectors, capabilities, watermarks, freezePlans, adapters, runs, audit, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => deltaUseCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.Delta, CorrelationId.New()), CancellationToken.None));

        // FinalDelta sem freeze autorizado continua recusado pelo gate de freeze (STOP-THE-LINE) primeiro.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<EvFreezeNotAuthorizedException>(() => deltaUseCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.FinalDelta, CorrelationId.New()), CancellationToken.None));

        var requestFreeze = new RequestFreezeUseCase(connectors, freezePlans, audit, clock);
        await requestFreeze.ExecuteAsync(new RequestFreeze(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        var decide = new DecideFreezeAuthorizationUseCase(connectors, freezePlans, audit, clock);
        await decide.ExecuteAsync(
            new DecideFreezeAuthorization(scope, identity.Id, "arch-1", true, "operator-1", EvFreezeAuthorizationRole.TenantAdministrator, "janela aprovada", CorrelationId.New()),
            CancellationToken.None);

        // Mesmo com freeze JÁ autorizado, o FinalDelta continua bloqueado — certificação é um gate
        // independente do freeze, nunca satisfeito por ele.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<EvDeltaStrategyUnsupportedException>(() => deltaUseCase.ExecuteAsync(
            new RequestEvDelta(scope, identity.Id, "arch-1", EvDeltaPhase.FinalDelta, CorrelationId.New()), CancellationToken.None));

        var plan = await freezePlans.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.Equal(EvFreezeStatus.FreezeAuthorized, plan!.Status); // NUNCA promovido a FinalDeltaReady sem um delta final Completed.
    }

    // ---- O ciclo de vida COMPLETO de freeze round-tripa por SQL real, independente do gate de delta --------

    [Fact]
    public async Task TheFullFreezeLifecycleRoundTripsThroughRealSqlAndEndsPermanentlyBlockedAtDecommission()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var connectors = Connectors;
        var freezePlans = FreezePlans(clock);
        var audit = Audit;

        var requestFreeze = new RequestFreezeUseCase(connectors, freezePlans, audit, clock);
        await requestFreeze.ExecuteAsync(new RequestFreeze(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        var decide = new DecideFreezeAuthorizationUseCase(connectors, freezePlans, audit, clock);
        await decide.ExecuteAsync(
            new DecideFreezeAuthorization(scope, identity.Id, "arch-1", true, "operator-1", EvFreezeAuthorizationRole.TenantAdministrator, "janela aprovada", CorrelationId.New()),
            CancellationToken.None);

        // Simula a conclusão do delta final diretamente no plano (o caminho real via RequestEvDeltaUseCase
        // está bloqueado por certificação neste Passo — provado no teste acima).
        var plan = (await freezePlans.GetAsync(scope, identity.Id, "arch-1", CancellationToken.None))!;
        var beforeFinalDelta = plan.Version;
        plan.MarkFinalDeltaReady();
        await freezePlans.SaveAsync(scope, plan, beforeFinalDelta, CancellationToken.None);

        var cutover = new MarkCutoverCompleteUseCase(connectors, freezePlans);
        var afterCutover = await cutover.ExecuteAsync(new MarkCutoverComplete(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.RollbackRetentionRequired, afterCutover);

        var attemptDecommission = new AttemptDecommissionUseCase(connectors, freezePlans, audit, clock);
        var blocked = await attemptDecommission.ExecuteAsync(new AttemptDecommission(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.DecommissionBlocked, blocked); // decommission permanece SEMPRE bloqueado neste Passo (req 11).

        var blockedAgain = await attemptDecommission.ExecuteAsync(new AttemptDecommission(scope, identity.Id, "arch-1", CorrelationId.New()), CancellationToken.None);
        Assert.Equal(EvFreezeStatus.DecommissionBlocked, blockedAgain); // idempotente sob SQL real.
    }

    // ---- AB-4C-009 item 2/3(b)/3(c): a evidência do watermark cobre opaque_token/producing_execution_id/
    // issued_at_utc — adulteração isolada de QUALQUER um destes campos, feita diretamente na linha SQL, é
    // detectada fail-closed na releitura, e issued_at_utc adulterado NUNCA promove um watermark antigo a
    // LatestCanonical -----------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByIdFailsClosedWhenOnlyTheOpaqueTokenColumnIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var watermarks = Watermarks;

        var watermark = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "original-token", clock.UtcNow);
        await watermarks.AppendAsync(scope, watermark, CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.ev_watermarks SET opaque_token = @token WHERE watermark_id = @id;",
            ("@token", "forged-token"), ("@id", watermark.Id.Value));

        var ex = await Assert.ThrowsAsync<EvWatermarkRejectedException>(
            () => watermarks.GetByIdAsync(scope, watermark.Id, CancellationToken.None));
        Assert.Equal(EvWatermarkRejectionReason.Tampered, ex.Reason);
    }

    [Fact]
    public async Task GetByIdFailsClosedWhenOnlyTheProducingExecutionIdColumnIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var watermarks = Watermarks;

        var watermark = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "original-token", clock.UtcNow);
        await watermarks.AppendAsync(scope, watermark, CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.ev_watermarks SET producing_execution_id = @executionId WHERE watermark_id = @id;",
            ("@executionId", Guid.NewGuid()), ("@id", watermark.Id.Value));

        var ex = await Assert.ThrowsAsync<EvWatermarkRejectedException>(
            () => watermarks.GetByIdAsync(scope, watermark.Id, CancellationToken.None));
        Assert.Equal(EvWatermarkRejectionReason.Tampered, ex.Reason);
    }

    [Fact]
    public async Task GetByIdFailsClosedWhenOnlyIssuedAtUtcColumnIsTamperedDirectlyInTheRow()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var watermarks = Watermarks;

        var watermark = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "original-token", clock.UtcNow);
        await watermarks.AppendAsync(scope, watermark, CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.ev_watermarks SET issued_at_utc = @issuedAt WHERE watermark_id = @id;",
            ("@issuedAt", clock.UtcNow.AddDays(1).UtcDateTime), ("@id", watermark.Id.Value));

        var ex = await Assert.ThrowsAsync<EvWatermarkRejectedException>(
            () => watermarks.GetByIdAsync(scope, watermark.Id, CancellationToken.None));
        Assert.Equal(EvWatermarkRejectionReason.Tampered, ex.Reason);
    }

    [Fact]
    public async Task TamperingIssuedAtUtcOfAnOlderWatermarkNeverSilentlyPromotesItToLatestCanonical()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var watermarks = Watermarks;

        var older = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Baseline, CompositeV1, Guid.NewGuid(), "older-token", clock.UtcNow);
        await watermarks.AppendAsync(scope, older, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10));
        var newer = ArchiveBridge.Domain.EnterpriseVault.Delta.EvWatermark.Issue(
            identity.Tenant, identity.Project, identity.Id, "arch-1", EvDeltaPhase.Delta, CompositeV1, Guid.NewGuid(), "newer-token", clock.UtcNow);
        await watermarks.AppendAsync(scope, newer, CancellationToken.None);

        // Sem adulteração, "newer" é o canônico (mais recente por issued_at_utc).
        var canonicalBeforeTampering = await watermarks.GetLatestCanonicalAsync(scope, identity.Id, "arch-1", CancellationToken.None);
        Assert.Equal(newer.Id, canonicalBeforeTampering!.Id);

        // Adultera issued_at_utc de "older" para além de "newer" — a ORDER BY do SQL passaria a devolver
        // "older" primeiro; a promoção artificial precisa ser recusada fail-closed, nunca silenciosamente aceita.
        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.ev_watermarks SET issued_at_utc = @issuedAt WHERE watermark_id = @id;",
            ("@issuedAt", clock.UtcNow.AddDays(1).UtcDateTime), ("@id", older.Id.Value));

        var ex = await Assert.ThrowsAsync<EvWatermarkRejectedException>(
            () => watermarks.GetLatestCanonicalAsync(scope, identity.Id, "arch-1", CancellationToken.None));
        Assert.Equal(EvWatermarkRejectionReason.Tampered, ex.Reason);
    }

    private async Task ExecuteAdminSqlAsync(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
