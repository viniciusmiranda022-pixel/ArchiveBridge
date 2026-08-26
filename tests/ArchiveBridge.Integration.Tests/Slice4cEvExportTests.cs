using ArchiveBridge.Application.EnterpriseVault.Export;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.EnterpriseVault.Connector;
using ArchiveBridge.Infrastructure.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4C, Passo 2 (AB-4C-005) — fundação de EXECUÇÃO de export EV sobre SQL Server real: idempotência do
/// pedido pela identidade CANÔNICA (item 5), throttling exclusivo por connector/archive (item 4), história
/// de tentativas append-only com manifesto/oversized (items 11/12/14), pipeline ponta a ponta com adapter
/// FAKE (nunca Enterprise Vault real) e revalidação de replay (item 13). Requer o serviço SQL Server do CI
/// oficial (<c>SqlServerCollectionDefinition</c>) — não executável neste ambiente sandbox sem docker.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4cEvExportTests(SqlServerFixture fixture)
{
    private static readonly WorkerId Worker = new("ev-export-worker-e2e");

    // Instante fixo (mesma convenção de Slice2Support.Now/Slice3Support): DateTimeOffset.UtcNow carrega
    // fração de milissegundo genuína, que o SQL Server ARREDONDA (não trunca) ao persistir em
    // DATETIME2(3) — o parâmetro @now sem escala explícita permanece com a fração completa, então
    // next_attempt_at_utc <= @now falha de forma intermitente exatamente quando o arredondamento do banco
    // empurra o valor gravado para cima do @now não arredondado. Um instante alinhado ao segundo elimina a
    // fração e torna o claim determinístico, como em todas as outras suítes SQL do repositório.
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private SqlConnectorRegistry Connectors => new(fixture.Factory);

    private SqlConnectorCapabilityStore Capabilities => new(fixture.Factory);

    private SqlEvExportCommandInbox Inbox(MutableClock clock) => new(fixture.Factory, clock);

    private SqlEvExportThrottleLeaseStore Throttle(MutableClock clock) => new(fixture.Factory, clock);

    private SqlEvExportAttemptStore Attempts(MutableClock clock) => new(fixture.Factory, clock);

    private SqlEvExportAuditTrail Audit => new(fixture.Factory);

    private SqlEvExportPendingScopeReader ScopeReader(MutableClock clock) => new(fixture.Factory, clock);

    // Nenhuma família é Certified por padrão na matriz embarcada (honestidade comercial, ADR-0013) — para
    // exercitar o caminho "capability certificada" nestes testes de EXECUÇÃO (Passo 2), o handshake é
    // construído diretamente via Rehydrate (uso legítimo da camada de persistência) em vez de Evaluate.
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

    // Cria o pedido pai (dbo.ev_export_requests) exigido por FK_ev_export_attempts_request. Os testes da
    // seção "História de tentativas" abaixo exercitam o STORE de tentativas isoladamente, mas uma tentativa
    // só pode existir sob um pedido de verdade já persistido — nunca um ExportRequestId solto/nunca
    // enfileirado (o append-only de dbo.ev_export_attempts referencia dbo.ev_export_requests por design).
    private async Task<ExportRequestId> EnqueueRequestAsync(
        TenantScope scope, ConnectorIdentity identity, string externalArchiveId, MutableClock clock)
    {
        var policy = ExportRequestPolicy.Default;
        var command = new EvExportCommand(
            scope, identity.Id, externalArchiveId, policy.MaxThreads, policy.MaxPstSizeMb, "operator", CorrelationId.New());
        var key = EvExportRequestIdentity.Compute(scope.Tenant, scope.Project, identity.Id, externalArchiveId, policy).ToIdempotencyKey();
        var enqueued = await Inbox(clock).EnqueueIdempotentAsync(command, key, CancellationToken.None);
        return enqueued.RequestId;
    }

    // ---- Idempotência por identidade CANÔNICA (item 5) --------------------------------------------------

    [Fact]
    public async Task DuplicateConcurrentRequestsWithTheSameCanonicalIdentityConvergeToOneLogicalRequest()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var inbox = Inbox(clock);
        var policy = ExportRequestPolicy.Default;
        var canonicalKey = EvExportRequestIdentity.Compute(scope.Tenant, scope.Project, identity.Id, "arch-1", policy).ToIdempotencyKey();
        var command = new EvExportCommand(scope, identity.Id, "arch-1", policy.MaxThreads, policy.MaxPstSizeMb, "operator", CorrelationId.New());

        var first = await inbox.EnqueueIdempotentAsync(command, canonicalKey, CancellationToken.None);
        var second = await inbox.EnqueueIdempotentAsync(command, canonicalKey, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.True(second.Replayed);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(first.JobId, second.JobId);
    }

    [Fact]
    public async Task ADifferentCanonicalIdentityCreatesASeparateLogicalRequest()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var inbox = Inbox(clock);
        var policy = ExportRequestPolicy.Default;

        var keyA = EvExportRequestIdentity.Compute(scope.Tenant, scope.Project, identity.Id, "arch-1", policy).ToIdempotencyKey();
        var keyB = EvExportRequestIdentity.Compute(scope.Tenant, scope.Project, identity.Id, "arch-2", policy).ToIdempotencyKey();
        var commandA = new EvExportCommand(scope, identity.Id, "arch-1", policy.MaxThreads, policy.MaxPstSizeMb, "operator", CorrelationId.New());
        var commandB = new EvExportCommand(scope, identity.Id, "arch-2", policy.MaxThreads, policy.MaxPstSizeMb, "operator", CorrelationId.New());

        var resultA = await inbox.EnqueueIdempotentAsync(commandA, keyA, CancellationToken.None);
        var resultB = await inbox.EnqueueIdempotentAsync(commandB, keyB, CancellationToken.None);

        Assert.True(resultA.Created);
        Assert.True(resultB.Created);
        Assert.NotEqual(resultA.RequestId, resultB.RequestId);
    }

    [Fact]
    public async Task ClaimNextReturnsTheEnqueuedCommandWithMatchingRequestId()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var inbox = Inbox(clock);
        var policy = ExportRequestPolicy.Default;
        var command = new EvExportCommand(scope, identity.Id, "arch-1", policy.MaxThreads, policy.MaxPstSizeMb, "operator", CorrelationId.New());
        var key = EvExportRequestIdentity.Compute(scope.Tenant, scope.Project, identity.Id, "arch-1", policy).ToIdempotencyKey();
        var enqueued = await inbox.EnqueueIdempotentAsync(command, key, CancellationToken.None);

        var claimed = await inbox.TryClaimNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(enqueued.RequestId, claimed!.Request);
        Assert.Equal("arch-1", claimed.Command.ExternalArchiveId);
    }

    // ---- Throttling exclusivo por connector E archive (item 4) --------------------------------------

    [Fact]
    public async Task AcquiringTheSameConnectorTwiceIsThrottled()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();

        var first = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);
        var second = await throttle.TryAcquireAsync(
            scope, connector, "arch-2", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second); // MESMO connector, archive diferente — ainda assim throttled.
    }

    [Fact]
    public async Task AcquiringTheSameArchiveTwiceIsThrottledEvenFromDifferentConnectors()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);

        var first = await throttle.TryAcquireAsync(
            scope, ConnectorId.New(), "shared-archive", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);
        var second = await throttle.TryAcquireAsync(
            scope, ConnectorId.New(), "shared-archive", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task ReleasingALeaseAllowsANewAcquisition()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();

        var first = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);
        Assert.NotNull(first);
        await throttle.ReleaseAsync(scope, first!, CancellationToken.None);

        var second = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ReleasingALeaseTwiceIsIdempotent()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var lease = await throttle.TryAcquireAsync(
            scope, ConnectorId.New(), "arch-1", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);

        await throttle.ReleaseAsync(scope, lease!, CancellationToken.None);
        await throttle.ReleaseAsync(scope, lease!, CancellationToken.None); // não lança
    }

    // ---- Recuperação de lease órfão/expirado (AB-4C-007 blocker 1) -----------------------------------

    [Fact]
    public async Task AnExpiredLeaseIsReclaimedByANewAcquisitionEvenWithoutAnExplicitRelease()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();
        var lease = TimeSpan.FromMinutes(1);

        var first = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(first);

        // Simula um worker que morreu ANTES de liberar (nenhum ReleaseAsync foi chamado) — o slot NUNCA
        // pode ficar órfão permanentemente.
        clock.Advance(lease + TimeSpan.FromSeconds(1));

        var second = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first!.LeaseId, second!.LeaseId);
    }

    [Fact]
    public async Task ALeaseStillWithinItsDeadlineIsNeverStolen()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();
        var lease = TimeSpan.FromMinutes(5);

        var first = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(first);

        clock.Advance(lease - TimeSpan.FromSeconds(1)); // ainda dentro do deadline
        var second = await throttle.TryAcquireAsync(
            scope, connector, "arch-2", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.Null(second);
    }

    [Fact]
    public async Task TwoConcurrentAcquisitionsAfterExpiryProduceExactlyOneWinner()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var connector = ConnectorId.New();
        var lease = TimeSpan.FromMinutes(1);

        var first = await Throttle(clock).TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(first);
        clock.Advance(lease + TimeSpan.FromSeconds(1));

        var results = await Task.WhenAll(
            Throttle(clock).TryAcquireAsync(scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None),
            Throttle(clock).TryAcquireAsync(scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None));

        Assert.Single(results, r => r is not null); // corrida de dois reclaimers ⇒ exatamente UM vencedor.
    }

    [Fact]
    public async Task RenewingBeforeExpiryExtendsTheDeadlineAndPreventsReclaim()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();
        var lease = TimeSpan.FromMinutes(1);

        var acquired = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(acquired);

        clock.Advance(TimeSpan.FromSeconds(50)); // ainda dentro do deadline original (60s)
        var renewed = await throttle.TryRenewAsync(scope, acquired!, lease, CancellationToken.None);
        Assert.True(renewed);

        clock.Advance(TimeSpan.FromSeconds(20)); // passou do deadline ORIGINAL (70s), mas não do renovado (110s)
        var stolen = await throttle.TryAcquireAsync(
            scope, connector, "arch-2", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.Null(stolen);
    }

    [Fact]
    public async Task RenewingAnAlreadyExpiredLeaseFails()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();
        var lease = TimeSpan.FromMinutes(1);

        var acquired = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(acquired);

        clock.Advance(lease + TimeSpan.FromSeconds(1));
        var renewed = await throttle.TryRenewAsync(scope, acquired!, lease, CancellationToken.None);
        Assert.False(renewed); // nunca ressuscita um lease já vencido.
    }

    [Fact]
    public async Task AnOldOwnerCannotReleaseOrRenewALeaseAlreadyReassumedByANewOwner()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var throttle = Throttle(clock);
        var connector = ConnectorId.New();
        var lease = TimeSpan.FromMinutes(1);

        var first = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(first);

        clock.Advance(lease + TimeSpan.FromSeconds(1));
        var second = await throttle.TryAcquireAsync(
            scope, connector, "arch-1", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first!.LeaseId, second!.LeaseId);

        // O titular ANTIGO não consegue renovar o slot já reassumido (não é "seu" mais).
        var renewedByOldOwner = await throttle.TryRenewAsync(scope, first, lease, CancellationToken.None);
        Assert.False(renewedByOldOwner);

        // Nem liberá-lo — a liberação do titular antigo é um NO-OP (idempotente), nunca afeta o novo titular.
        await throttle.ReleaseAsync(scope, first, CancellationToken.None);
        var stillHeldByNewOwner = await throttle.TryAcquireAsync(
            scope, connector, "arch-2", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.Null(stillHeldByNewOwner); // o slot do connector ainda pertence ao SEGUNDO titular.

        // O titular ATUAL, por sua vez, consegue liberar normalmente.
        await throttle.ReleaseAsync(scope, second, CancellationToken.None);
        var freedByCurrentOwner = await throttle.TryAcquireAsync(
            scope, connector, "arch-3", ExportAttemptId.New(), CorrelationId.New(), lease, CancellationToken.None);
        Assert.NotNull(freedByCurrentOwner);
    }

    // ---- História de tentativas append-only (items 11/12/14) ----------------------------------------

    [Fact]
    public async Task AppendedCompletedAttemptRoundTripsTheManifestAndOversizedItems()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var attempts = Attempts(clock);
        var request = await EnqueueRequestAsync(scope, identity, "arch-1", clock);
        var attempt = ExportAttemptId.New();
        var manifest = EvExportManifest.Create(
            request, attempt, "arch-1", [new EvExportManifestEntry("out1.pst", 1024, DeterministicHash.Compute(["h1"]))], "15.0", "1.0.0");
        var oversized = new[]
        {
            new EvOversizedNativeItem(
                "item-ref-1", EvOversizedNativeItem.NativeExportThresholdBytes + 1,
                EvOversizedNativeItemClassification.ExceedsNativeExportThreshold, "diag", clock.UtcNow),
        };
        var record = new EvExportAttemptRecord(
            request, attempt, 1, identity.Id, "arch-1", EvExportAttemptOutcome.Completed,
            new EvExportResultCode(EvExportResultCodes.ExportCompleted), null, manifest, oversized, 0, clock.UtcNow, clock.UtcNow);

        await attempts.AppendAsync(scope, record, fence: null, CancellationToken.None);

        var latest = await attempts.GetLatestAsync(scope, request, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(manifest.ManifestHash, latest!.Manifest!.ManifestHash);
        Assert.Single(latest.Manifest.Entries);
        Assert.Single(latest.OversizedItems);
        Assert.Equal("item-ref-1", latest.OversizedItems[0].ExternalItemReference);
    }

    [Fact]
    public async Task RetryPreservesAttemptHistoryAcrossMultipleAppends()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);
        var attempts = Attempts(clock);
        var request = await EnqueueRequestAsync(scope, identity, "arch-1", clock);

        var failed = new EvExportAttemptRecord(
            request, ExportAttemptId.New(), 1, identity.Id, "arch-1", EvExportAttemptOutcome.Failed,
            new EvExportResultCode(EvExportResultCodes.ExportFailed), "PROCESS_EXIT_NON_ZERO", null, [], 1, clock.UtcNow, clock.UtcNow);
        await attempts.AppendAsync(scope, failed, fence: null, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        var manifest = EvExportManifest.Create(
            request, ExportAttemptId.New(), "arch-1", [new EvExportManifestEntry("out1.pst", 10, DeterministicHash.Compute(["h2"]))], "15.0", "1.0.0");
        var completed = new EvExportAttemptRecord(
            request, manifest.Attempt, 2, identity.Id, "arch-1", EvExportAttemptOutcome.Completed,
            new EvExportResultCode(EvExportResultCodes.ExportCompleted), null, manifest, [], 0, clock.UtcNow, clock.UtcNow);
        await attempts.AppendAsync(scope, completed, fence: null, CancellationToken.None);

        var history = await attempts.ListAttemptsAsync(scope, request, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(EvExportAttemptOutcome.Failed, history[0].Outcome);
        Assert.Equal(EvExportAttemptOutcome.Completed, history[1].Outcome);

        var latest = await attempts.GetLatestAsync(scope, request, CancellationToken.None);
        Assert.Equal(EvExportAttemptOutcome.Completed, latest!.Outcome);
    }

    // ---- Pipeline ponta a ponta com adapter FAKE (nunca EV real) -------------------------------------

    [Fact]
    public async Task RequestFlowsDurablyThroughTheProcessorToACompletedManifestWithoutRealEv()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);

        var requestUseCase = new RequestEvExportUseCase(
            Connectors, Capabilities, Inbox(clock), Audit, EvExportPolicy.Default, clock);
        var requested = await requestUseCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);
        Assert.True(requested.Created);

        Assert.Contains(scope, await ScopeReader(clock).ListEligibleScopesAsync(100_000, CancellationToken.None));

        var hash = DeterministicHash.Compute(["fake-output-content"]);
        var executor = new FakeExecutor(new EvExportProcessResult(0, false, false, "15.0", "1.0.0", [], null));
        var inspector = new FakeInspector([new EvExportOutputCandidate("out1.pst", 1024, hash)]);
        var processor = new EvExportCommandProcessor(
            Inbox(clock), fixture.Store(clock), fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease),
            Connectors, Capabilities, Throttle(clock), executor, inspector, Attempts(clock), Audit,
            EvExportPolicy.Default, Path.GetTempPath(), clock, RetryPolicy.Default);

        var execution = await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(EvExportCommandOutcome.Completed, execution!.Outcome);

        var job = await fixture.Store(clock).GetAsync(scope, requested.JobId, CancellationToken.None);
        Assert.Equal(JobState.Completed, job!.State);

        var resultUseCase = new GetEvExportResultUseCase(Attempts(clock), inspector, Audit, Path.GetTempPath());
        var manifest = await resultUseCase.ExecuteAsync(scope, requested.RequestId, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Single(manifest!.Entries);
    }

    [Fact]
    public async Task CapabilityRevokedBetweenRequestAndExecutionBlocksFailClosed()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);

        var requestUseCase = new RequestEvExportUseCase(Connectors, Capabilities, Inbox(clock), Audit, EvExportPolicy.Default, clock);
        var requested = await requestUseCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);

        // O connector é revogado DEPOIS do enfileiramento, ANTES da execução — item 8.
        await Connectors.RevokeAsync(scope, identity.Id, clock.UtcNow, CancellationToken.None);

        var executor = new FakeExecutor(new EvExportProcessResult(0, false, false, "15.0", "1.0.0", [], null));
        var inspector = new FakeInspector([]);
        var processor = new EvExportCommandProcessor(
            Inbox(clock), fixture.Store(clock), fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease),
            Connectors, Capabilities, Throttle(clock), executor, inspector, Attempts(clock), Audit,
            EvExportPolicy.Default, Path.GetTempPath(), clock, RetryPolicy.Default);

        var execution = await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(EvExportCommandOutcome.Failed, execution!.Outcome);
        Assert.False(executor.WasCalled); // NUNCA invoca o executor quando bloqueado por capability (item 8).

        var latest = await Attempts(clock).GetLatestAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.Equal(EvExportAttemptOutcome.CapabilityBlocked, latest!.Outcome);
    }

    [Fact]
    public async Task ReplayAfterTheOutputWasRemovedFailsClosed()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);

        var requestUseCase = new RequestEvExportUseCase(Connectors, Capabilities, Inbox(clock), Audit, EvExportPolicy.Default, clock);
        var requested = await requestUseCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);

        var hash = DeterministicHash.Compute(["fake-output-content"]);
        var executor = new FakeExecutor(new EvExportProcessResult(0, false, false, "15.0", "1.0.0", [], null));
        var inspectorAtWriteTime = new FakeInspector([new EvExportOutputCandidate("out1.pst", 1024, hash)]);
        var processor = new EvExportCommandProcessor(
            Inbox(clock), fixture.Store(clock), fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease),
            Connectors, Capabilities, Throttle(clock), executor, inspectorAtWriteTime, Attempts(clock), Audit,
            EvExportPolicy.Default, Path.GetTempPath(), clock, RetryPolicy.Default);
        await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);

        // O output físico "desaparece" (adulteração/remoção fora da aplicação) antes do replay.
        var inspectorAtReplayTime = new FakeInspector([]);
        var resultUseCase = new GetEvExportResultUseCase(Attempts(clock), inspectorAtReplayTime, Audit, Path.GetTempPath());

        await Assert.ThrowsAsync<EvExportIntegrityViolationException>(
            () => resultUseCase.ExecuteAsync(scope, requested.RequestId, CorrelationId.New(), CancellationToken.None));
    }

    // ---- Throttled attempt persistido + RetryScheduled auditado (AB-4C-007 blockers 2/3) -------------

    [Fact]
    public async Task AThrottledAttemptIsPersistedWithRetryScheduledAuditedAndTheRetryPreservesLineage()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);

        var requestUseCase = new RequestEvExportUseCase(Connectors, Capabilities, Inbox(clock), Audit, EvExportPolicy.Default, clock);
        var requested = await requestUseCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);

        // Um outro titular já detém o throttle do MESMO connector (archive diferente) — a primeira
        // tentativa do processor será bloqueada por concorrência antes de invocar o executor.
        var blocker = await Throttle(clock).TryAcquireAsync(
            scope, identity.Id, "other-archive", ExportAttemptId.New(), CorrelationId.New(), Slice2Support.Lease, CancellationToken.None);
        Assert.NotNull(blocker);

        var executor = new FakeExecutor(new EvExportProcessResult(0, false, false, "15.0", "1.0.0", [], null));
        var inspector = new FakeInspector([new EvExportOutputCandidate("out1.pst", 1024, DeterministicHash.Compute(["out1"]))]);
        var processor = new EvExportCommandProcessor(
            Inbox(clock), fixture.Store(clock), fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease),
            Connectors, Capabilities, Throttle(clock), executor, inspector, Attempts(clock), Audit,
            EvExportPolicy.Default, Path.GetTempPath(), clock, RetryPolicy.Default);

        var firstExecution = await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(firstExecution);
        Assert.Equal(EvExportCommandOutcome.Retried, firstExecution!.Outcome);
        Assert.False(executor.WasCalled); // bloqueado ANTES de qualquer efeito externo.

        // (blocker 3) A tentativa throttled NUNCA some do evidence chain — aparece no attempt history.
        var afterFirst = await Attempts(clock).ListAttemptsAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.Single(afterFirst);
        Assert.Equal(EvExportAttemptOutcome.Throttled, afterFirst[0].Outcome);
        Assert.Equal(1, afterFirst[0].AttemptNumber);

        // (blocker 2) RetryScheduled é auditado quando o retry foi de fato agendado (Applied) — sem duplicar.
        var auditReader = new SqlEvExportAuditReader(fixture.Factory);
        var eventsAfterFirst = await auditReader.GetEventsAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.Contains(eventsAfterFirst, e => e.EventCode == EvExportAuditEventCode.Throttled);
        Assert.Single(eventsAfterFirst, e => e.EventCode == EvExportAuditEventCode.RetryScheduled);

        // Libera o bloqueio e avança além do backoff de throttle (15s) — o retry deve convergir com outro
        // AttemptNumber, preservando toda a lineage anterior.
        await Throttle(clock).ReleaseAsync(scope, blocker!, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(16));

        var secondExecution = await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(secondExecution);
        Assert.Equal(EvExportCommandOutcome.Completed, secondExecution!.Outcome);
        Assert.True(executor.WasCalled);

        var finalHistory = await Attempts(clock).ListAttemptsAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.Equal(2, finalHistory.Count);
        Assert.Equal(EvExportAttemptOutcome.Throttled, finalHistory[0].Outcome);
        Assert.Equal(1, finalHistory[0].AttemptNumber);
        Assert.Equal(EvExportAttemptOutcome.Completed, finalHistory[1].Outcome);
        Assert.Equal(2, finalHistory[1].AttemptNumber); // outro AttemptNumber — nunca sobrescreve a evidência anterior.
    }

    [Fact]
    public async Task RetryScheduledIsAlsoAuditedForATransientProcessFailure()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);

        var requestUseCase = new RequestEvExportUseCase(Connectors, Capabilities, Inbox(clock), Audit, EvExportPolicy.Default, clock);
        var requested = await requestUseCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);

        // Exit code não-zero: falha TRANSITÓRIA do processo do exporter (candidata a retry), não bloqueio.
        var executor = new FakeExecutor(new EvExportProcessResult(1, false, false, "15.0", "1.0.0", [], "PROCESS_EXIT_NON_ZERO"));
        var inspector = new FakeInspector([]);
        var processor = new EvExportCommandProcessor(
            Inbox(clock), fixture.Store(clock), fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease),
            Connectors, Capabilities, Throttle(clock), executor, inspector, Attempts(clock), Audit,
            EvExportPolicy.Default, Path.GetTempPath(), clock, RetryPolicy.Default);

        var execution = await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(EvExportCommandOutcome.Retried, execution!.Outcome);
        Assert.True(executor.WasCalled);

        var latest = await Attempts(clock).GetLatestAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.Equal(EvExportAttemptOutcome.Failed, latest!.Outcome);

        var events = await new SqlEvExportAuditReader(fixture.Factory).GetEventsAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.Single(events, e => e.EventCode == EvExportAuditEventCode.RetryScheduled);
    }

    [Fact]
    public async Task AProcessResultThatTimesOutDespiteAZeroExitCodeIsNeverTreatedAsCompleted()
    {
        // AB-I7-001 chaos case 173/#10 (provider retornando resultado ambíguo): um exit code 0, isolado,
        // sugeriria sucesso — mas o processo do exporter também estourou o timeout configurado. A
        // ambiguidade nunca é resolvida a favor do sucesso: o mesmo tratamento de
        // PurviewUploadCommandProcessor (ver PurviewUploadUseCaseTests) se aplica aqui.
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Now);
        var identity = await RegisterExportCapableConnectorAsync(scope, clock);

        var requestUseCase = new RequestEvExportUseCase(Connectors, Capabilities, Inbox(clock), Audit, EvExportPolicy.Default, clock);
        var requested = await requestUseCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);

        var executor = new FakeExecutor(new EvExportProcessResult(0, true, false, "15.0", "1.0.0", [], "PROCESS_TIMEOUT"));
        var inspector = new FakeInspector([]);
        var processor = new EvExportCommandProcessor(
            Inbox(clock), fixture.Store(clock), fixture.LeaseManager(clock, RetryPolicy.Default, Slice2Support.Lease),
            Connectors, Capabilities, Throttle(clock), executor, inspector, Attempts(clock), Audit,
            EvExportPolicy.Default, Path.GetTempPath(), clock, RetryPolicy.Default);

        var execution = await processor.ProcessNextAsync(scope, Worker, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.NotEqual(EvExportCommandOutcome.Completed, execution!.Outcome);
        Assert.Equal(EvExportCommandOutcome.Retried, execution.Outcome);

        var latest = await Attempts(clock).GetLatestAsync(scope, requested.RequestId, CancellationToken.None);
        Assert.NotEqual(EvExportAttemptOutcome.Completed, latest!.Outcome);
    }

    private sealed class FakeExecutor(EvExportProcessResult result) : IEvArchiveExportExecutor
    {
        public bool WasCalled { get; private set; }

        public Task<EvExportProcessResult> RunAsync(EvExportProcessRequest request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeInspector(IReadOnlyList<EvExportOutputCandidate> candidates) : IEvExportOutputInspector
    {
        public Task<IReadOnlyList<EvExportOutputCandidate>> ScanPstOutputsAsync(
            EvExportOutputLocation location, CancellationToken cancellationToken) =>
            Task.FromResult(candidates);
    }
}
