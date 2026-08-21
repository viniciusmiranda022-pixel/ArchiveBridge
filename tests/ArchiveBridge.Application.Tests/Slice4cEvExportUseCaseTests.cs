using ArchiveBridge.Application.EnterpriseVault.Export;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Testes de Application da fundação de EXECUÇÃO de export EV (Slice 4C, Passo 2, AB-4C-005) — provam que
/// os casos de uso são testáveis só com Domain + Contracts, sem qualquer implementação de Infrastructure
/// (nunca PowerShell/processo real).
/// </summary>
public sealed class Slice4cEvExportUseCaseTests
{
    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static ConnectorIdentity ActiveConnector(TenantScope scope, DateTimeOffset now) =>
        ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('a', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);

    private static ConnectorCapabilityHandshake ExportCapableHandshake(ConnectorIdentity identity, DateTimeOffset now) =>
        ConnectorCapabilityHandshake.Rehydrate(
            CapabilityHandshakeId.New(), identity.Id, identity.Tenant, identity.Project, "15.0.0", true,
            ConnectorSupportLevel.Certified, exportCapable: true, blockingReason: null, CorrelationId.New(), now);

    // ---- RequestEvExportUseCase (items 1/2/3/5/8) ----------------------------------------------------

    [Fact]
    public async Task ConnectorNotFoundIsIndistinguishableFromCrossTenant()
    {
        var scope = Scope();
        var useCase = new RequestEvExportUseCase(
            new FakeConnectorRegistryForExport(), new FakeConnectorCapabilityStoreForExport(),
            new FakeEvExportCommandInbox(), new FakeEvExportAuditTrail(), EvExportPolicy.Default, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ConnectorNotFoundException>(() => useCase.ExecuteAsync(
            new RequestEvExport(scope, ConnectorId.New(), "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task RevokedConnectorFailsClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistryForExport();
        var identity = ActiveConnector(scope, now).Revoke(now);
        connectors.Seed(identity);

        var useCase = new RequestEvExportUseCase(
            connectors, new FakeConnectorCapabilityStoreForExport(), new FakeEvExportCommandInbox(), new FakeEvExportAuditTrail(),
            EvExportPolicy.Default, new StubClock(now));

        await Assert.ThrowsAsync<ConnectorRevokedException>(() => useCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task MissingCapabilityHandshakeBlocksFailClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistryForExport();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);

        var useCase = new RequestEvExportUseCase(
            connectors, new FakeConnectorCapabilityStoreForExport(), new FakeEvExportCommandInbox(), new FakeEvExportAuditTrail(),
            EvExportPolicy.Default, new StubClock(now));

        await Assert.ThrowsAsync<EvExportCapabilityBlockedException>(() => useCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task NonCertifiedHandshakeBlocksFailClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistryForExport();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        var capabilities = new FakeConnectorCapabilityStoreForExport();
        capabilities.Seed(ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), identity.Id, identity.Tenant, identity.Project, "99.0.0", true, CorrelationId.New(), now));

        var useCase = new RequestEvExportUseCase(
            connectors, capabilities, new FakeEvExportCommandInbox(), new FakeEvExportAuditTrail(),
            EvExportPolicy.Default, new StubClock(now));

        var exception = await Assert.ThrowsAsync<EvExportCapabilityBlockedException>(() => useCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None));
        Assert.Equal("SCHEMA_VERSION_UNKNOWN", exception.BlockingReason);
    }

    [Fact]
    public async Task PolicyAboveTheEffectiveEnvelopeIsRejected()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistryForExport();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        var capabilities = new FakeConnectorCapabilityStoreForExport();
        capabilities.Seed(ExportCapableHandshake(identity, now));
        var narrowPolicy = EvExportPolicy.Create(1, effectiveMaxThreads: 4, ExportRequestPolicy.DefaultMaxPstSizeMb, TimeSpan.FromHours(1), 1024);

        var useCase = new RequestEvExportUseCase(
            connectors, capabilities, new FakeEvExportCommandInbox(), new FakeEvExportAuditTrail(), narrowPolicy, new StubClock(now));

        await Assert.ThrowsAsync<EvExportValidationException>(() => useCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, 8, "operator", CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task HappyPathEnqueuesIdempotentlyByCanonicalIdentity()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistryForExport();
        var identity = ActiveConnector(scope, now);
        connectors.Seed(identity);
        var capabilities = new FakeConnectorCapabilityStoreForExport();
        capabilities.Seed(ExportCapableHandshake(identity, now));
        var inbox = new FakeEvExportCommandInbox();

        var useCase = new RequestEvExportUseCase(
            connectors, capabilities, inbox, new FakeEvExportAuditTrail(), EvExportPolicy.Default, new StubClock(now));

        var first = await useCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            new RequestEvExport(scope, identity.Id, "arch-1", null, null, "operator", CorrelationId.New()), CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.True(second.Replayed);
        Assert.Equal(first.RequestId, second.RequestId); // retry do MESMO pedido lógico converge (item 5)
        Assert.Equal(2, inbox.EnqueueCallCount); // ambas as chamadas atingem o inbox; só UM pedido é criado
        Assert.Equal(1, inbox.DistinctRequestCount);
    }

    // ---- EvExportEvaluator (montagem pura) -------------------------------------------------------------

    [Fact]
    public void EvaluateSuccessBuildsAManifestAndClassifiesOversizedItems()
    {
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var process = new EvExportProcessResult(
            0, TimedOut: false, OutputLimitExceeded: false, "15.0", "1.0.0",
            [new EvOversizedNativeItemRaw("item-ref", EvOversizedNativeItem.NativeExportThresholdBytes + 1, "diag")], null);
        var candidates = new[] { new EvExportOutputCandidate("out1.pst", 100, DeterministicHash.Compute(["h1"])) };

        var result = EvExportEvaluator.EvaluateSuccess(
            request, attempt, 1, "arch-1", process, candidates, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.True(result.IsSuccessful);
        Assert.Single(result.Manifest!.Entries);
        Assert.Single(result.OversizedItems);
        Assert.Equal(EvOversizedNativeItemClassification.ExceedsNativeExportThreshold, result.OversizedItems[0].Classification);
    }

    [Fact]
    public void EvaluateProcessFailureNeverProducesAManifest()
    {
        var process = new EvExportProcessResult(1, TimedOut: false, OutputLimitExceeded: false, "", "1.0.0", [], "EXPORT_FAILED");
        var result = EvExportEvaluator.EvaluateProcessFailure(
            ExportRequestId.New(), ExportAttemptId.New(), 1, process, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Manifest);
        Assert.Equal(EvExportAttemptOutcome.Failed, result.Outcome);
    }

    // ---- GetEvExportResultUseCase (item 13): replay revalida outputs físicos, fail-closed --------------

    [Fact]
    public async Task ReplayWithMatchingOutputsReturnsTheManifest()
    {
        var scope = Scope();
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var hash = DeterministicHash.Compute(["content"]);
        var entry = new EvExportManifestEntry("out.pst", 100, hash);
        var manifest = EvExportManifest.Create(request, attempt, "arch-1", [entry], "15.0", "1.0.0");
        var attempts = new FakeEvExportAttemptStore();
        attempts.Seed(new EvExportAttemptRecord(
            request, attempt, 1, ConnectorId.New(), "arch-1", EvExportAttemptOutcome.Completed,
            new EvExportResultCode(EvExportResultCodes.ExportCompleted), null, manifest, [], 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var inspector = new FakeEvExportOutputInspector([new EvExportOutputCandidate("out.pst", 100, hash)]);

        var useCase = new GetEvExportResultUseCase(attempts, inspector, new FakeEvExportAuditTrail(), "C:\\connector-root");
        var result = await useCase.ExecuteAsync(scope, request, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(manifest.ManifestHash, result!.ManifestHash);
    }

    [Fact]
    public async Task ReplayWithARemovedOutputFailsClosed()
    {
        var scope = Scope();
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var hash = DeterministicHash.Compute(["content"]);
        var manifest = EvExportManifest.Create(request, attempt, "arch-1", [new EvExportManifestEntry("out.pst", 100, hash)], "15.0", "1.0.0");
        var attempts = new FakeEvExportAttemptStore();
        attempts.Seed(new EvExportAttemptRecord(
            request, attempt, 1, ConnectorId.New(), "arch-1", EvExportAttemptOutcome.Completed,
            new EvExportResultCode(EvExportResultCodes.ExportCompleted), null, manifest, [], 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var inspector = new FakeEvExportOutputInspector([]); // arquivo removido do disco

        var useCase = new GetEvExportResultUseCase(attempts, inspector, new FakeEvExportAuditTrail(), "C:\\connector-root");

        await Assert.ThrowsAsync<EvExportIntegrityViolationException>(
            () => useCase.ExecuteAsync(scope, request, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task ReplayWithATamperedHashFailsClosed()
    {
        var scope = Scope();
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var hash = DeterministicHash.Compute(["content"]);
        var manifest = EvExportManifest.Create(request, attempt, "arch-1", [new EvExportManifestEntry("out.pst", 100, hash)], "15.0", "1.0.0");
        var attempts = new FakeEvExportAttemptStore();
        attempts.Seed(new EvExportAttemptRecord(
            request, attempt, 1, ConnectorId.New(), "arch-1", EvExportAttemptOutcome.Completed,
            new EvExportResultCode(EvExportResultCodes.ExportCompleted), null, manifest, [], 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var tamperedHash = DeterministicHash.Compute(["adulterated"]);
        var inspector = new FakeEvExportOutputInspector([new EvExportOutputCandidate("out.pst", 100, tamperedHash)]);

        var useCase = new GetEvExportResultUseCase(attempts, inspector, new FakeEvExportAuditTrail(), "C:\\connector-root");

        await Assert.ThrowsAsync<EvExportIntegrityViolationException>(
            () => useCase.ExecuteAsync(scope, request, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task NoCompletedAttemptReturnsNull()
    {
        var scope = Scope();
        var request = ExportRequestId.New();
        var useCase = new GetEvExportResultUseCase(
            new FakeEvExportAttemptStore(), new FakeEvExportOutputInspector([]), new FakeEvExportAuditTrail(), "C:\\connector-root");

        var result = await useCase.ExecuteAsync(scope, request, CorrelationId.New(), CancellationToken.None);
        Assert.Null(result);
    }
}

// ---- Duplos de teste (em memória, sem SQL/PowerShell) --------------------------------------------------

internal sealed class FakeConnectorRegistryForExport : IConnectorRegistry
{
    private readonly List<ConnectorIdentity> _identities = [];

    public void Seed(ConnectorIdentity identity) => _identities.Add(identity);

    public Task<ConnectorRegistrationResult> RegisterAsync(ConnectorIdentity identity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ConnectorIdentity?> GetAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        var found = _identities.FirstOrDefault(i => i.Id == connector && i.Tenant == scope.Tenant && i.Project == scope.Project);
        return Task.FromResult(found);
    }

    public Task RevokeAsync(TenantScope scope, ConnectorId connector, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var index = _identities.FindIndex(i => i.Id == connector && i.Tenant == scope.Tenant && i.Project == scope.Project);
        if (index < 0)
        {
            throw new ConnectorNotFoundException();
        }

        _identities[index] = _identities[index].Revoke(nowUtc);
        return Task.CompletedTask;
    }
}

internal sealed class FakeConnectorCapabilityStoreForExport : IConnectorCapabilityStore
{
    private readonly List<ConnectorCapabilityHandshake> _handshakes = [];

    public void Seed(ConnectorCapabilityHandshake handshake) => _handshakes.Add(handshake);

    public Task AppendAsync(ConnectorCapabilityHandshake handshake, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ConnectorCapabilityHandshake?> GetLatestAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        var latest = _handshakes
            .Where(h => h.Connector == connector && h.Tenant == scope.Tenant && h.Project == scope.Project)
            .OrderByDescending(h => h.CollectedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }
}

internal sealed class FakeEvExportCommandInbox : IEvExportCommandInbox
{
    private readonly Dictionary<Guid, (JobId Job, ExportRequestId Request, EvExportCommand Command)> _byKey = [];

    public int EnqueueCallCount { get; private set; }

    public int DistinctRequestCount => _byKey.Values.Select(v => v.Request).Distinct().Count();

    public Task<EvExportEnqueueResult> EnqueueIdempotentAsync(
        EvExportCommand command, Guid canonicalIdempotencyKey, CancellationToken cancellationToken)
    {
        EnqueueCallCount++;
        if (_byKey.TryGetValue(canonicalIdempotencyKey, out var existing))
        {
            return Task.FromResult(new EvExportEnqueueResult(existing.Job, existing.Request, Created: false, Replayed: true));
        }

        var jobId = JobId.New();
        var requestId = ExportRequestId.New();
        _byKey[canonicalIdempotencyKey] = (jobId, requestId, command);
        return Task.FromResult(new EvExportEnqueueResult(jobId, requestId, Created: true, Replayed: false));
    }

    public Task<ClaimedEvExportCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken) =>
        Task.FromResult<ClaimedEvExportCommand?>(null);
}

internal sealed class FakeEvExportAuditTrail : IEvExportAuditTrail
{
    public List<EvExportAuditEvent> Events { get; } = [];

    public Task AppendAsync(TenantScope scope, EvExportAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}

internal sealed class FakeEvExportAttemptStore : IEvExportAttemptStore
{
    private readonly List<EvExportAttemptRecord> _records = [];

    public void Seed(EvExportAttemptRecord record) => _records.Add(record);

    public Task AppendAsync(TenantScope scope, EvExportAttemptRecord record, JobFence? fence, CancellationToken cancellationToken)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<EvExportAttemptRecord?> GetLatestAsync(TenantScope scope, ExportRequestId request, CancellationToken cancellationToken) =>
        Task.FromResult(_records.Where(r => r.Request == request).OrderByDescending(r => r.AttemptNumber).FirstOrDefault());

    public Task<IReadOnlyList<EvExportAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, ExportRequestId request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvExportAttemptRecord>>(
            _records.Where(r => r.Request == request).OrderBy(r => r.AttemptNumber).ToList());
}

internal sealed class FakeEvExportOutputInspector(IReadOnlyList<EvExportOutputCandidate> candidates) : IEvExportOutputInspector
{
    public Task<IReadOnlyList<EvExportOutputCandidate>> ScanPstOutputsAsync(
        EvExportOutputLocation location, CancellationToken cancellationToken) =>
        Task.FromResult(candidates);
}
