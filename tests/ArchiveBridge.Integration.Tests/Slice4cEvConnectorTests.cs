using System.Data;
using ArchiveBridge.Application.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Infrastructure.EnterpriseVault.Connector;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4C, Passo 1 — fundação de connector EV sobre SQL real (AB-4C-001): enrollment token de uso
/// único (resgate/replay/expiração/revogação), registro idempotente de identidade, handshake de
/// capability append-only e snapshots de inventário versionados/idempotentes; isolamento por tenant/
/// projeto (RLS) em todos os stores novos.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4cEvConnectorTests(SqlServerFixture fixture)
{
    private SqlEnrollmentTokenStore Tokens => new(fixture.Factory);

    private SqlConnectorRegistry Connectors => new(fixture.Factory);

    private SqlConnectorCapabilityStore Capabilities => new(fixture.Factory);

    private SqlConnectorInventoryStore Inventory => new(fixture.Factory);

    private static Sha256Hash RandomHash() => new(Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0'));

    private async Task<EnrollmentToken> IssueTokenAsync(TenantScope scope, Sha256Hash hash, DateTimeOffset now, TimeSpan? ttl = null)
    {
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), scope.Tenant, scope.Project, hash, "operator@example.com",
            CorrelationId.New(), now, now + (ttl ?? EnrollmentToken.MaxTtl));
        await Tokens.IssueAsync(token, CancellationToken.None);
        return token;
    }

    // ---- Enrollment token: uso único, expiração, revogação (§15.1, AB-4C-001 critério 1) ------------

    [Fact]
    public async Task RedeemHappyPathConsumesTheTokenAndBindsTheConnector()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var hash = RandomHash();
        await IssueTokenAsync(scope, hash, now);
        var connector = ConnectorId.New();

        var consumed = await Tokens.RedeemAsync(hash, connector, now, CancellationToken.None);

        Assert.Equal(EnrollmentTokenStatus.Consumed, consumed.Status);
        Assert.Equal(connector, consumed.ConsumedByConnector);
        Assert.Equal(scope.Tenant, consumed.Tenant);
        Assert.Equal(scope.Project, consumed.Project);
    }

    [Fact]
    public async Task RedeemingTheSameTokenTwiceIsARejectedReplay()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var hash = RandomHash();
        await IssueTokenAsync(scope, hash, now);
        await Tokens.RedeemAsync(hash, ConnectorId.New(), now, CancellationToken.None);

        await Assert.ThrowsAsync<EnrollmentTokenNotConsumableException>(
            () => Tokens.RedeemAsync(hash, ConnectorId.New(), now, CancellationToken.None));
    }

    [Fact]
    public async Task RedeemingAnExpiredTokenFailsClosedEvenIfNeverConsumed()
    {
        var scope = SqlServerFixture.NewScope();
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var hash = RandomHash();
        await IssueTokenAsync(scope, hash, issuedAt, TimeSpan.FromMinutes(15)); // já expirado no passado

        await Assert.ThrowsAsync<EnrollmentTokenExpiredException>(
            () => Tokens.RedeemAsync(hash, ConnectorId.New(), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task RedeemingAnUnknownHashIsIndistinguishableFromAnyOtherInvalidToken()
    {
        await Assert.ThrowsAsync<EnrollmentTokenNotFoundException>(
            () => Tokens.RedeemAsync(RandomHash(), ConnectorId.New(), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task RevokedTokenCanNeverBeRedeemed()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var hash = RandomHash();
        var token = await IssueTokenAsync(scope, hash, now);
        await Tokens.RevokeAsync(scope, token.Id, CancellationToken.None);

        await Assert.ThrowsAsync<EnrollmentTokenNotConsumableException>(
            () => Tokens.RedeemAsync(hash, ConnectorId.New(), now, CancellationToken.None));
    }

    [Fact]
    public async Task RevokingATokenFromTheWrongProjectIsIndistinguishableFromNotFound()
    {
        var scope = SqlServerFixture.NewScope();
        var otherProjectScope = new TenantScope(scope.Tenant, SqlServerFixture.NewScope().Project);
        var now = DateTimeOffset.UtcNow;
        var token = await IssueTokenAsync(scope, RandomHash(), now);

        await Assert.ThrowsAsync<EnrollmentTokenNotFoundException>(
            () => Tokens.RevokeAsync(otherProjectScope, token.Id, CancellationToken.None));
    }

    // ---- ConnectorRegistry: registro idempotente por (tenant, projeto, thumbprint) -------------------

    private static ConnectorIdentity NewIdentity(TenantScope scope, ConnectorPublicKeyThumbprint thumbprint, DateTimeOffset now) =>
        ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, thumbprint, "host01", "Site-A", "1.0.0",
            EnrollmentTokenId.New(), now);

    [Fact]
    public async Task RegisterCreatesAndGetResolvesWithinScope()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('a', 64)), now);

        var result = await Connectors.RegisterAsync(identity, CancellationToken.None);
        var fetched = await Connectors.GetAsync(scope, identity.Id, CancellationToken.None);

        Assert.True(result.Created);
        Assert.NotNull(fetched);
        Assert.Equal(identity.Thumbprint, fetched!.Thumbprint);
    }

    [Fact]
    public async Task ReRegisteringTheSameThumbprintConvergesWithoutDuplicatingIdentity()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var thumbprint = new ConnectorPublicKeyThumbprint(new string('b', 64));
        var first = await Connectors.RegisterAsync(NewIdentity(scope, thumbprint, now), CancellationToken.None);

        var reInstall = ConnectorIdentity.Register(
            ConnectorId.New(), scope.Tenant, scope.Project, thumbprint, "host01-renamed", "Site-B", "2.0.0",
            EnrollmentTokenId.New(), now.AddDays(1));
        var second = await Connectors.RegisterAsync(reInstall, CancellationToken.None);

        Assert.False(second.Created);
        Assert.Equal(first.Identity.Id, second.Identity.Id);
        Assert.Equal("host01-renamed", second.Identity.Hostname);
        Assert.Equal("2.0.0", second.Identity.ConnectorVersion);
    }

    [Fact]
    public async Task GetFromAnotherProjectIsIndistinguishableFromNotFound()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('c', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var otherProjectScope = new TenantScope(scope.Tenant, SqlServerFixture.NewScope().Project);
        var fetched = await Connectors.GetAsync(otherProjectScope, identity.Id, CancellationToken.None);

        Assert.Null(fetched);
    }

    // ---- ConnectorCapabilityStore: append-only, o vigente é sempre o mais recente --------------------

    [Fact]
    public async Task GetLatestReturnsTheMostRecentHandshakeWithoutRewritingThePrevious()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('d', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var first = ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), identity.Id, scope.Tenant, scope.Project, "14.2.2", true, CorrelationId.New(), now);
        var second = ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), identity.Id, scope.Tenant, scope.Project, "15.0.0", false, CorrelationId.New(), now.AddHours(1));
        await Capabilities.AppendAsync(first, CancellationToken.None);
        await Capabilities.AppendAsync(second, CancellationToken.None);

        var latest = await Capabilities.GetLatestAsync(scope, identity.Id, CancellationToken.None);

        Assert.Equal(second.Id, latest!.Id);
        Assert.Equal("POWERSHELL_SNAPIN_UNAVAILABLE", latest.BlockingReason);
    }

    // ---- ConnectorInventoryStore: append-only, versionado, idempotente sob corrida -------------------

    private static InventorySnapshot NewSnapshot(ConnectorId connector, TenantScope scope, int version, DateTimeOffset now, string archiveId) =>
        InventorySnapshot.Create(
            InventorySnapshotId.New(), connector, scope.Tenant, scope.Project, version,
            [new InventoryArchiveRecord(archiveId, "Mailbox", "VaultStore-1", InventoryArchiveStatus.Active, ["EV_POWERSHELL_AVAILABLE"])],
            CorrelationId.New(), now);

    [Fact]
    public async Task AppendPersistsTheSnapshotAndItsArchivesRoundTrip()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('e', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        var snapshot = NewSnapshot(identity.Id, scope, 1, now, "arch-1");

        var result = await Inventory.AppendAsync(snapshot, CancellationToken.None);
        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);

        Assert.True(result.Created);
        Assert.NotNull(latest);
        Assert.Equal(snapshot.SnapshotHash, latest!.SnapshotHash);
        Assert.Equal("arch-1", Assert.Single(latest.Archives).ExternalArchiveId);
    }

    [Fact]
    public async Task ConcurrentAppendsOfTheSameConnectorVersionWithIdenticalContentConvergeToOneRowWithoutDuplicating()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('f', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        // MESMA versão, MESMO conteúdo lógico (mesmo archive id) — Id/InventorySnapshotId diferentes, mas
        // SnapshotHash igual: réplay concorrente genuíno (AB-4C-002 critério 7), não uma mudança perdida.
        var winner = NewSnapshot(identity.Id, scope, 1, now, "arch-1");
        var identicalReplay = NewSnapshot(identity.Id, scope, 1, now, "arch-1");
        Assert.Equal(winner.SnapshotHash, identicalReplay.SnapshotHash);

        var first = await Inventory.AppendAsync(winner, CancellationToken.None);
        var second = await Inventory.AppendAsync(identicalReplay, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(winner.Id, second.Snapshot.Id); // converge para o já persistido, nunca duplica

        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(1, latest!.Version);
        Assert.Equal("arch-1", Assert.Single(latest.Archives).ExternalArchiveId);
    }

    [Fact]
    public async Task ConcurrentAppendsOfTheSameConnectorVersionWithDifferentContentSurfaceAnExplicitConcurrencyConflict()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('3', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var winner = NewSnapshot(identity.Id, scope, 1, now, "arch-1");
        var loser = NewSnapshot(identity.Id, scope, 1, now, "arch-2"); // MESMA versão, conteúdo DIFERENTE

        var first = await Inventory.AppendAsync(winner, CancellationToken.None);

        // AB-4C-002: uma versão colidida com conteúdo diferente NUNCA é tratada como réplay (o que
        // silenciosamente perderia "arch-2") — o store falha fechado com um sinal de concorrência explícito
        // e retriable para o chamador reler o latest e tentar de novo com a próxima versão livre.
        await Assert.ThrowsAsync<ConcurrencyException>(() => Inventory.AppendAsync(loser, CancellationToken.None));

        Assert.True(first.Created);
        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(1, latest!.Version);
        Assert.Equal("arch-1", Assert.Single(latest.Archives).ExternalArchiveId); // evidência do winner intacta
    }

    // ---- SubmitInventorySnapshotUseCase sobre SQL real: convergência determinística sob corrida real ----

    private static InventoryArchiveRecord ArchiveRecord(string externalId) =>
        new(externalId, "Mailbox", "VaultStore-1", InventoryArchiveStatus.Active, []);

    [Fact]
    public async Task ConcurrentSubmissionsOfDifferentInventoriesFromTheSameLatestBothPersistAtDistinctVersions()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('4', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var firstUseCase = new SubmitInventorySnapshotUseCase(
            Connectors, Inventory, new StubEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [ArchiveRecord("arch-A")])),
            new MutableClock(now));
        var secondUseCase = new SubmitInventorySnapshotUseCase(
            Connectors, Inventory, new StubEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [ArchiveRecord("arch-B")])),
            new MutableClock(now));

        var firstTask = firstUseCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);
        var secondTask = secondUseCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);
        var results = await Task.WhenAll(firstTask, secondTask);

        // Nenhuma das duas mudanças reais foi descartada como réplay: ambas Created=true, em versões
        // distintas — nunca uma delas apontando Created=false para o conteúdo da outra (AB-4C-002).
        Assert.True(results[0].Created);
        Assert.True(results[1].Created);
        Assert.NotEqual(results[0].Snapshot.Version, results[1].Snapshot.Version);

        var versions = results.Select(r => r.Snapshot.Version).OrderBy(v => v).ToArray();
        Assert.Equal([1, 2], versions);

        // Qual submissão convergiu para a versão 1 (vs. 2) depende da corrida real — não é assumido aqui,
        // só que AMBOS os conteúdos sobreviveram, cada um na sua própria versão.
        var archiveIds = results.Select(r => Assert.Single(r.Snapshot.Archives).ExternalArchiveId).ToHashSet();
        Assert.Equal(new HashSet<string> { "arch-A", "arch-B" }, archiveIds);

        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(2, latest!.Version);
    }

    [Fact]
    public async Task ConcurrentSubmissionsOfIdenticalInventoriesConvergeToASingleLogicalSnapshot()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('5', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var probeResult = new EvInventoryProbeResult("14.2.2", true, [ArchiveRecord("arch-same")]);
        var firstUseCase = new SubmitInventorySnapshotUseCase(
            Connectors, Inventory, new StubEvInventoryAdapter(probeResult), new MutableClock(now));
        var secondUseCase = new SubmitInventorySnapshotUseCase(
            Connectors, Inventory, new StubEvInventoryAdapter(probeResult), new MutableClock(now));

        var firstTask = firstUseCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);
        var secondTask = secondUseCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, identity.Id, CorrelationId.New()), CancellationToken.None);
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.True(results[0].Created ^ results[1].Created); // exatamente UMA linha nova; a outra convergiu
        Assert.Equal(results[0].Snapshot.Id, results[1].Snapshot.Id);
        Assert.Equal(1, results[0].Snapshot.Version);
        Assert.Equal(1, results[1].Snapshot.Version);

        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(1, latest!.Version);
    }

    [Fact]
    public async Task GetLatestReturnsTheHighestVersionAcrossMultipleSnapshots()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('1', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        await Inventory.AppendAsync(NewSnapshot(identity.Id, scope, 1, now, "arch-1"), CancellationToken.None);
        var v2 = NewSnapshot(identity.Id, scope, 2, now.AddHours(1), "arch-2");
        await Inventory.AppendAsync(v2, CancellationToken.None);

        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);

        Assert.Equal(2, latest!.Version);
        Assert.Equal(v2.SnapshotHash, latest.SnapshotHash);
    }

    [Fact]
    public async Task InventoryIsIsolatedAcrossProjectsOfTheSameTenant()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('2', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        await Inventory.AppendAsync(NewSnapshot(identity.Id, scope, 1, now, "arch-1"), CancellationToken.None);

        var otherProjectScope = new TenantScope(scope.Tenant, SqlServerFixture.NewScope().Project);
        var latest = await Inventory.GetLatestAsync(otherProjectScope, identity.Id, CancellationToken.None);

        Assert.Null(latest);
    }

    // ---- InventorySnapshot: a persistência é fronteira NÃO CONFIÁVEL (AB-4C-003) ----------------------
    //
    // Cada cenário abaixo persiste um snapshot válido pela aplicação e então o corrompe POR FORA (via a
    // identidade administrativa, nunca pela aplicação — ab_app só tem SELECT/INSERT nestas tabelas), de um
    // jeito que nenhum CHECK row-local consegue recusar. Reidratar (GetLatestAsync) e o réplay idempotente
    // do caso de uso (que releé exatamente o mesmo latest) têm de falhar fechado, nunca devolver a linha
    // corrompida como snapshot canônico.

    private async Task<InventorySnapshotAppendResult> SeedBaselineSnapshotAsync(
        TenantScope scope, ConnectorId connector, DateTimeOffset now, params string[] archiveIds)
    {
        var archives = archiveIds
            .Select(id => new InventoryArchiveRecord(id, "Mailbox", "VaultStore-1", InventoryArchiveStatus.Active, ["EV_POWERSHELL_AVAILABLE"]))
            .ToArray();
        var snapshot = InventorySnapshot.Create(
            InventorySnapshotId.New(), connector, scope.Tenant, scope.Project, 1, archives, CorrelationId.New(), now);
        return await Inventory.AppendAsync(snapshot, CancellationToken.None);
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

    /// <summary>Prova que a leitura e o réplay idempotente falham fechado contra o snapshot corrompido informado.</summary>
    private async Task AssertCorruptSnapshotFailsClosedAsync(TenantScope scope, ConnectorId connector, DateTimeOffset now)
    {
        await Assert.ThrowsAsync<InventorySnapshotIntegrityViolationException>(
            () => Inventory.GetLatestAsync(scope, connector, CancellationToken.None));

        // O réplay do caso de uso releé exatamente o mesmo latest corrompido — também tem de falhar fechado,
        // nunca gravar uma segunda versão por cima nem devolver o conteúdo corrompido como convergido.
        var useCase = new SubmitInventorySnapshotUseCase(
            Connectors, Inventory,
            new StubEvInventoryAdapter(new EvInventoryProbeResult("14.2.2", true, [ArchiveRecord("arch-replay-probe")])),
            new MutableClock(now));
        await Assert.ThrowsAsync<InventorySnapshotIntegrityViolationException>(() => useCase.ExecuteAsync(
            new SubmitInventorySnapshotRequest(scope, connector, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenPersistedArchiveCountDivergesFromLoadedChildren()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('7', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        var seeded = await SeedBaselineSnapshotAsync(scope, identity.Id, now, "arch-1");

        // O header persistido passa a afirmar 2 archives — nenhum CHECK row-local consegue recusar isto,
        // só um único filho realmente existe. archive_count deixa de ser dado morto.
        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.ev_connector_inventory_snapshots SET archive_count = 2 WHERE snapshot_id = @snapshot;",
            ("@snapshot", seeded.Snapshot.Id.Value));

        await AssertCorruptSnapshotFailsClosedAsync(scope, identity.Id, now);
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenAChildArchiveIsRemovedButTheStoredHashAndCountStayStale()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('8', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        var seeded = await SeedBaselineSnapshotAsync(scope, identity.Id, now, "arch-1", "arch-2");

        // Um dos dois filhos é removido por fora — snapshot_hash e archive_count do header continuam
        // afirmando os DOIS archives originais.
        await ExecuteAdminSqlAsync(
            scope,
            "DELETE FROM dbo.ev_connector_inventory_archives WHERE snapshot_id = @snapshot AND external_archive_id = @external;",
            ("@snapshot", seeded.Snapshot.Id.Value), ("@external", "arch-2"));

        await AssertCorruptSnapshotFailsClosedAsync(scope, identity.Id, now);
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenAChildArchiveIsAlteredButTheStoredHashAndCountStayStale()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('9', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        var seeded = await SeedBaselineSnapshotAsync(scope, identity.Id, now, "arch-1");

        // O único filho é alterado por fora (status muda de Active=1 para Inactive=2) — archive_count
        // continua batendo (ainda 1 filho), só o CONTEÚDO diverge do que o snapshot_hash gravado afirma.
        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.ev_connector_inventory_archives SET status = 2 WHERE snapshot_id = @snapshot AND external_archive_id = @external;",
            ("@snapshot", seeded.Snapshot.Id.Value), ("@external", "arch-1"));

        await AssertCorruptSnapshotFailsClosedAsync(scope, identity.Id, now);
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenTheStoredSnapshotHashIsForgedButChildrenStayIntact()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('0', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        var seeded = await SeedBaselineSnapshotAsync(scope, identity.Id, now, "arch-1");

        // Os filhos permanecem exatamente como gravados — só o snapshot_hash do header é forjado.
        var forgedHash = DeterministicHash.Compute(["not-the-real-snapshot-hash"]);
        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.ev_connector_inventory_snapshots SET snapshot_hash = @hash WHERE snapshot_id = @snapshot;",
            ("@hash", forgedHash.Value), ("@snapshot", seeded.Snapshot.Id.Value));

        await AssertCorruptSnapshotFailsClosedAsync(scope, identity.Id, now);
    }

    [Fact]
    public async Task GetLatestOfAnUntamperedSnapshotStillRoundTripsAfterTheIntegrityChecksWereAdded()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('c', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);
        var seeded = await SeedBaselineSnapshotAsync(scope, identity.Id, now, "arch-2", "arch-1");

        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);

        Assert.Equal(seeded.Snapshot.SnapshotHash, latest!.SnapshotHash);
        Assert.Equal(["arch-1", "arch-2"], latest.Archives.Select(a => a.ExternalArchiveId));
    }
}

/// <summary>Duplo de teste da porta <see cref="IEvInventoryAdapter"/> — determinístico, sem PowerShell.</summary>
internal sealed class StubEvInventoryAdapter(EvInventoryProbeResult result) : IEvInventoryAdapter
{
    public Task<EvInventoryProbeResult> ProbeAsync(ConnectorId connector, CorrelationId correlation, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}
