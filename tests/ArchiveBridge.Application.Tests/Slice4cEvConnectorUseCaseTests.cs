using System.Security.Cryptography;
using ArchiveBridge.Application.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Testes de Application da fundação de connector EV (Slice 4C, Passo 1, AB-4C-001) — provam que os casos
/// de uso são testáveis só com Domain + Contracts, sem qualquer implementação de Infrastructure.
/// </summary>
public sealed class Slice4cEvConnectorUseCaseTests
{
    private static TenantScope Scope() => new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static (string RawSecret, EnrollmentToken Token) IssueToken(
        FakeEnrollmentTokenStore store, TenantScope scope, DateTimeOffset now)
    {
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var rawSecret = Convert.ToHexStringLower(secretBytes);
        var hash = DeterministicHash.ComputeBytes(secretBytes);
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), scope.Tenant, scope.Project, hash, "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);
        store.IssueAsync(token, CancellationToken.None).GetAwaiter().GetResult();
        return (rawSecret, token);
    }

    // ---- IssueEnrollmentTokenUseCase ----------------------------------------------------------------

    [Fact]
    public async Task IssueEnrollmentTokenProducesARawSecretWhoseHashMatchesTheStoredToken()
    {
        var scope = Scope();
        var store = new FakeEnrollmentTokenStore();
        var now = DateTimeOffset.UtcNow;
        var useCase = new IssueEnrollmentTokenUseCase(store, new StubClock(now));

        var result = await useCase.ExecuteAsync(
            new IssueEnrollmentTokenRequest(scope, "operator@example.com", CorrelationId.New()), CancellationToken.None);

        var expectedHash = DeterministicHash.ComputeBytes(Convert.FromHexString(result.RawSecret));
        Assert.Equal(expectedHash, store.Single().TokenHash);
        Assert.Equal(now + EnrollmentToken.MaxTtl, result.ExpiresAtUtc);
    }

    // ---- RegisterConnectorUseCase (§15.1, AB-4C-001 critérios 1-3) ----------------------------------

    [Fact]
    public async Task RegisterConnectorHappyPathDerivesScopeEntirelyFromTheToken()
    {
        var scope = Scope();
        var tokens = new FakeEnrollmentTokenStore();
        var connectors = new FakeConnectorRegistry();
        var now = DateTimeOffset.UtcNow;
        var (rawSecret, _) = IssueToken(tokens, scope, now);
        var useCase = new RegisterConnectorUseCase(tokens, connectors, new StubClock(now));

        var result = await useCase.ExecuteAsync(
            new RegisterConnectorRequest(
                rawSecret, new ConnectorPublicKeyThumbprint(new string('a', 64)), "host01", "Site-A", "1.0.0", CorrelationId.New()),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(scope.Tenant, result.Identity.Tenant);
        Assert.Equal(scope.Project, result.Identity.Project);
    }

    [Fact]
    public async Task RegisterConnectorMalformedSecretIsIndistinguishableFromUnknownToken()
    {
        var tokens = new FakeEnrollmentTokenStore();
        var connectors = new FakeConnectorRegistry();
        var useCase = new RegisterConnectorUseCase(tokens, connectors, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<EnrollmentTokenNotFoundException>(() => useCase.ExecuteAsync(
            new RegisterConnectorRequest(
                "not-hexadecimal", new ConnectorPublicKeyThumbprint(new string('a', 64)), "host01", "Site-A", "1.0.0", CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task RegisterConnectorUnknownSecretThrowsNotFound()
    {
        var tokens = new FakeEnrollmentTokenStore();
        var connectors = new FakeConnectorRegistry();
        var useCase = new RegisterConnectorUseCase(tokens, connectors, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<EnrollmentTokenNotFoundException>(() => useCase.ExecuteAsync(
            new RegisterConnectorRequest(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
                new ConnectorPublicKeyThumbprint(new string('a', 64)), "host01", "Site-A", "1.0.0", CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task RegisterConnectorSameThumbprintTwiceConvergesToTheSameConnectorId()
    {
        var scope = Scope();
        var tokens = new FakeEnrollmentTokenStore();
        var connectors = new FakeConnectorRegistry();
        var now = DateTimeOffset.UtcNow;
        var thumbprint = new ConnectorPublicKeyThumbprint(new string('b', 64));
        var useCase = new RegisterConnectorUseCase(tokens, connectors, new StubClock(now));

        var (firstSecret, _) = IssueToken(tokens, scope, now);
        var first = await useCase.ExecuteAsync(
            new RegisterConnectorRequest(firstSecret, thumbprint, "host01", "Site-A", "1.0.0", CorrelationId.New()),
            CancellationToken.None);

        var (secondSecret, _) = IssueToken(tokens, scope, now);
        var second = await useCase.ExecuteAsync(
            new RegisterConnectorRequest(secondSecret, thumbprint, "host01-renamed", "Site-A", "1.1.0", CorrelationId.New()),
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Identity.Id, second.Identity.Id);
        Assert.Equal("host01-renamed", second.Identity.Hostname);
    }

    // ---- SubmitConnectorCapabilityHandshakeUseCase (AB-4C-001 critérios 4/5) ------------------------

    [Fact]
    public async Task SubmitCapabilityHandshakeUnknownConnectorThrowsNotFound()
    {
        var scope = Scope();
        var connectors = new FakeConnectorRegistry();
        var capabilities = new FakeConnectorCapabilityStore();
        var useCase = new SubmitConnectorCapabilityHandshakeUseCase(connectors, capabilities, new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ConnectorNotFoundException>(() => useCase.ExecuteAsync(
            new SubmitConnectorCapabilityHandshakeRequest(scope, ConnectorId.New(), "14.2.2", true, CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task SubmitCapabilityHandshakeRevokedConnectorFailsClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('c', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);
        connectors.ForceRevoke(identity.Id, now);

        var capabilities = new FakeConnectorCapabilityStore();
        var useCase = new SubmitConnectorCapabilityHandshakeUseCase(connectors, capabilities, new StubClock(now));

        await Assert.ThrowsAsync<ConnectorRevokedException>(() => useCase.ExecuteAsync(
            new SubmitConnectorCapabilityHandshakeRequest(scope, identity.Id, "14.2.2", true, CorrelationId.New()),
            CancellationToken.None));
    }

    [Fact]
    public async Task SubmitCapabilityHandshakeHappyPathPersistsTheEvaluatedHandshake()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('d', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var capabilities = new FakeConnectorCapabilityStore();
        var useCase = new SubmitConnectorCapabilityHandshakeUseCase(connectors, capabilities, new StubClock(now));

        var handshake = await useCase.ExecuteAsync(
            new SubmitConnectorCapabilityHandshakeRequest(scope, identity.Id, "14.2.2", true, CorrelationId.New()),
            CancellationToken.None);

        Assert.False(handshake.ExportCapable); // nenhuma família é Certified por padrão (honestidade comercial)
        var latest = await capabilities.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(handshake.Id, latest!.Id);
    }

    // ---- SubmitInventorySnapshotUseCase (AB-4C-001 critérios 6/7/8) ---------------------------------

    private static InventoryArchiveRecord Archive(string externalId) =>
        new(externalId, "Mailbox", "VaultStore-1", InventoryArchiveStatus.Active, []);

    [Fact]
    public async Task SubmitInventorySnapshotFirstSubmissionCreatesVersion1()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('e', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var inventory = new FakeConnectorInventoryStore();
        var adapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-1")]));
        var useCase = new SubmitInventorySnapshotUseCase(connectors, inventory, adapter, new StubClock(now));

        var result = await useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(1, result.Snapshot.Version);
        Assert.Equal(1, inventory.AppendCallCount);
    }

    [Fact]
    public async Task SubmitInventorySnapshotIdenticalResubmissionIsAnIdempotentReplayWithoutANewRow()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('f', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var inventory = new FakeConnectorInventoryStore();
        var adapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-1")]));
        var useCase = new SubmitInventorySnapshotUseCase(connectors, inventory, adapter, new StubClock(now));

        var first = await useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Snapshot.Id, second.Snapshot.Id);
        Assert.Equal(1, second.Snapshot.Version);
        Assert.Equal(1, inventory.AppendCallCount); // nenhuma linha nova foi gravada no réplay
    }

    [Fact]
    public async Task SubmitInventorySnapshotChangedArchivesCreatesANewVersionWithoutRewritingThePrevious()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('1', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var inventory = new FakeConnectorInventoryStore();
        var firstAdapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-1")]));
        var firstUseCase = new SubmitInventorySnapshotUseCase(connectors, inventory, firstAdapter, new StubClock(now));
        var first = await firstUseCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);

        var secondAdapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-1"), Archive("arch-2")]));
        var secondUseCase = new SubmitInventorySnapshotUseCase(connectors, inventory, secondAdapter, new StubClock(now.AddHours(1)));
        var second = await secondUseCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);

        Assert.True(second.Created);
        Assert.Equal(2, second.Snapshot.Version);
        Assert.NotEqual(first.Snapshot.SnapshotHash, second.Snapshot.SnapshotHash);
        Assert.Equal(2, inventory.AppendCallCount);
    }

    // ---- AB-4C-002: corrida em SubmitInventorySnapshotUseCase — mudança real nunca vira réplay falso ---

    [Fact]
    public async Task SubmitInventorySnapshotRetriesAndConvergesWhenAConcurrentWriterTakesTheComputedVersionWithDifferentContent()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('3', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var inventory = new FakeConnectorInventoryStore();
        var adapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-mine")]));
        var useCase = new SubmitInventorySnapshotUseCase(connectors, inventory, adapter, new StubClock(now));

        var injected = false;
        inventory.BeforeAppendAttempt = candidate =>
        {
            if (!injected && candidate.Version == 1)
            {
                injected = true;

                // Simula OUTRO writer publicando a versão 1 com conteúdo DIFERENTE exatamente entre a
                // leitura do latest feita pelo useCase e este append — a corrida que AB-4C-002 corrige.
                inventory.SeedDirectly(InventorySnapshot.Create(
                    InventorySnapshotId.New(), identity.Id, identity.Tenant, identity.Project, 1,
                    [Archive("arch-competitor")], CorrelationId.New(), now));
            }
        };

        var result = await useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);

        // A mudança real (arch-mine) nunca é descartada como réplay falso: converge na próxima versão livre.
        Assert.True(result.Created);
        Assert.Equal(2, result.Snapshot.Version);
        Assert.Equal("arch-mine", Assert.Single(result.Snapshot.Archives).ExternalArchiveId);

        var latest = await inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(2, latest!.Version);
        Assert.Equal("arch-mine", Assert.Single(latest.Archives).ExternalArchiveId);

        // Append-only: a 1ª tentativa (versão 1) colide e lança; a 2ª (versão 2) persiste — nada é reescrito.
        Assert.Equal(2, inventory.AppendCallCount);
    }

    [Fact]
    public async Task SubmitInventorySnapshotIdenticalConcurrentContentConvergesWithoutRetryingAsANewVersion()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('4', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var inventory = new FakeConnectorInventoryStore();
        var adapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-1")]));
        var useCase = new SubmitInventorySnapshotUseCase(connectors, inventory, adapter, new StubClock(now));

        var injected = false;
        inventory.BeforeAppendAttempt = candidate =>
        {
            if (!injected && candidate.Version == 1)
            {
                injected = true;

                // Outro writer publica a MESMA versão com o MESMO conteúdo lógico (hash igual, Id/correlação
                // diferentes) — réplay concorrente genuíno, não uma mudança real perdida.
                inventory.SeedDirectly(InventorySnapshot.Create(
                    InventorySnapshotId.New(), identity.Id, identity.Tenant, identity.Project, 1,
                    [Archive("arch-1")], CorrelationId.New(), now));
            }
        };

        var result = await useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);

        Assert.False(result.Created);
        Assert.Equal(1, result.Snapshot.Version);
        Assert.Equal("arch-1", Assert.Single(result.Snapshot.Archives).ExternalArchiveId);
    }

    [Fact]
    public async Task SubmitInventorySnapshotFailsClosedWhenConcurrentContentionNeverConverges()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('5', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);

        var inventory = new FakeConnectorInventoryStore();
        var adapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-mine")]));
        var useCase = new SubmitInventorySnapshotUseCase(connectors, inventory, adapter, new StubClock(now));

        // Um "writer" adversarial ocupa CADA versão candidate, sempre com conteúdo diferente do nosso —
        // contenção que nunca converge dentro do limite de tentativas.
        var nextCompetitorArchiveSuffix = 0;
        inventory.BeforeAppendAttempt = candidate =>
        {
            nextCompetitorArchiveSuffix++;
            inventory.SeedDirectly(InventorySnapshot.Create(
                InventorySnapshotId.New(), identity.Id, identity.Tenant, identity.Project, candidate.Version,
                [Archive($"arch-competitor-{nextCompetitorArchiveSuffix}")], CorrelationId.New(), now));
        };

        await Assert.ThrowsAsync<ConcurrencyException>(() => useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task SubmitInventorySnapshotRevokedConnectorFailsClosed()
    {
        var scope = Scope();
        var now = DateTimeOffset.UtcNow;
        var connectors = new FakeConnectorRegistry();
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, new ConnectorPublicKeyThumbprint(new string('2', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        await connectors.RegisterAsync(identity, CancellationToken.None);
        connectors.ForceRevoke(identity.Id, now);

        var inventory = new FakeConnectorInventoryStore();
        var adapter = new FakeEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [Archive("arch-1")]));
        var useCase = new SubmitInventorySnapshotUseCase(connectors, inventory, adapter, new StubClock(now));

        await Assert.ThrowsAsync<ConnectorRevokedException>(() => useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None));
    }
}

/// <summary>Duplo de teste da porta <see cref="IEnrollmentTokenStore"/> — em memória, sem SQL.</summary>
internal sealed class FakeEnrollmentTokenStore : IEnrollmentTokenStore
{
    private readonly List<EnrollmentToken> _tokens = [];

    public EnrollmentToken Single() => _tokens.Single();

    public Task IssueAsync(EnrollmentToken token, CancellationToken cancellationToken)
    {
        _tokens.Add(token);
        return Task.CompletedTask;
    }

    public Task<EnrollmentToken> RedeemAsync(
        Sha256Hash tokenHash, ConnectorId connector, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var index = _tokens.FindIndex(t => t.TokenHash == tokenHash);
        if (index < 0)
        {
            throw new EnrollmentTokenNotFoundException();
        }

        var consumed = _tokens[index].Consume(connector, nowUtc);
        _tokens[index] = consumed;
        return Task.FromResult(consumed);
    }

    public Task RevokeAsync(TenantScope scope, EnrollmentTokenId id, CancellationToken cancellationToken)
    {
        var index = _tokens.FindIndex(t => t.Id == id && t.Tenant == scope.Tenant && t.Project == scope.Project);
        if (index < 0)
        {
            throw new EnrollmentTokenNotFoundException();
        }

        _tokens[index] = _tokens[index].Revoke();
        return Task.CompletedTask;
    }
}

/// <summary>Duplo de teste da porta <see cref="IConnectorRegistry"/> — em memória, sem SQL.</summary>
internal sealed class FakeConnectorRegistry : IConnectorRegistry
{
    private readonly List<ConnectorIdentity> _identities = [];

    public Task<ConnectorRegistrationResult> RegisterAsync(ConnectorIdentity identity, CancellationToken cancellationToken)
    {
        var index = _identities.FindIndex(i =>
            i.Tenant == identity.Tenant && i.Project == identity.Project && i.Thumbprint == identity.Thumbprint);
        if (index >= 0)
        {
            var reRegistered = _identities[index].ReRegister(
                identity.Hostname, identity.SiteName, identity.ConnectorVersion, identity.EnrolledViaToken, identity.UpdatedAtUtc);
            _identities[index] = reRegistered;
            return Task.FromResult(new ConnectorRegistrationResult(reRegistered, Created: false));
        }

        _identities.Add(identity);
        return Task.FromResult(new ConnectorRegistrationResult(identity, Created: true));
    }

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

    /// <summary>Auxiliar de teste: revoga diretamente sem passar pelo escopo (usada pelos testes já existentes).</summary>
    public void ForceRevoke(ConnectorId connector, DateTimeOffset nowUtc)
    {
        var index = _identities.FindIndex(i => i.Id == connector);
        _identities[index] = _identities[index].Revoke(nowUtc);
    }
}

/// <summary>Duplo de teste da porta <see cref="IConnectorCapabilityStore"/> — em memória, sem SQL.</summary>
internal sealed class FakeConnectorCapabilityStore : IConnectorCapabilityStore
{
    private readonly List<ConnectorCapabilityHandshake> _handshakes = [];

    public Task AppendAsync(ConnectorCapabilityHandshake handshake, CancellationToken cancellationToken)
    {
        _handshakes.Add(handshake);
        return Task.CompletedTask;
    }

    public Task<ConnectorCapabilityHandshake?> GetLatestAsync(
        TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        var latest = _handshakes
            .Where(h => h.Connector == connector && h.Tenant == scope.Tenant && h.Project == scope.Project)
            .OrderByDescending(h => h.CollectedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }
}

/// <summary>
/// Duplo de teste da porta <see cref="IConnectorInventoryStore"/> — em memória, sem SQL. Reproduz
/// fielmente a semântica de concorrência exigida de <c>SqlConnectorInventoryStore</c> (AB-4C-002): uma
/// colisão de versão só converge (<c>Created=false</c>) quando o hash já persistido é IGUAL ao candidate;
/// hash diferente lança <see cref="ConcurrencyException"/>, nunca mascara a mudança como réplay.
/// </summary>
internal sealed class FakeConnectorInventoryStore : IConnectorInventoryStore
{
    private readonly List<InventorySnapshot> _snapshots = [];

    public int AppendCallCount { get; private set; }

    /// <summary>
    /// Hook de teste: invocado imediatamente antes de CADA tentativa de append, ainda dentro da mesma
    /// chamada a <see cref="AppendAsync"/> — permite simular outra escrita concorrente que ocupa a versão
    /// candidate entre a leitura do latest (pelo chamador) e este append, exercitando o retry da
    /// Application de forma determinística (sem depender de threads reais).
    /// </summary>
    public Action<InventorySnapshot>? BeforeAppendAttempt { get; set; }

    public Task<InventorySnapshot?> GetLatestAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        var latest = _snapshots
            .Where(s => s.Connector == connector && s.Tenant == scope.Tenant && s.Project == scope.Project)
            .OrderByDescending(s => s.Version)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<InventorySnapshotAppendResult> AppendAsync(InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        BeforeAppendAttempt?.Invoke(snapshot);
        AppendCallCount++;

        var existing = _snapshots.FirstOrDefault(s => s.Connector == snapshot.Connector && s.Version == snapshot.Version);
        if (existing is not null)
        {
            if (existing.SnapshotHash == snapshot.SnapshotHash)
            {
                return Task.FromResult(new InventorySnapshotAppendResult(existing, Created: false));
            }

            throw new ConcurrencyException(
                $"Versão {snapshot.Version} já ocupada por outro snapshot com hash diferente.");
        }

        _snapshots.Add(snapshot);
        return Task.FromResult(new InventorySnapshotAppendResult(snapshot, Created: true));
    }

    /// <summary>Auxiliar de teste: injeta diretamente um snapshot "de outro writer", sem passar por AppendAsync/hook.</summary>
    public void SeedDirectly(InventorySnapshot snapshot) => _snapshots.Add(snapshot);
}

/// <summary>Duplo de teste da porta <see cref="IEvInventoryAdapter"/> — determinístico, sem PowerShell.</summary>
internal sealed class FakeEvInventoryAdapter(EvInventoryProbeResult result) : IEvInventoryAdapter
{
    public Task<EvInventoryProbeResult> ProbeAsync(ConnectorId connector, CorrelationId correlation, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}
