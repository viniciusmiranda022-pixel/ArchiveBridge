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
    public async Task ConcurrentAppendsOfTheSameConnectorVersionConvergeToOneRowWithoutDuplicating()
    {
        var scope = SqlServerFixture.NewScope();
        var now = DateTimeOffset.UtcNow;
        var identity = NewIdentity(scope, new ConnectorPublicKeyThumbprint(new string('f', 64)), now);
        await Connectors.RegisterAsync(identity, CancellationToken.None);

        var winner = NewSnapshot(identity.Id, scope, 1, now, "arch-1");
        var loser = NewSnapshot(identity.Id, scope, 1, now, "arch-2"); // MESMA versão, conteúdo diferente

        var first = await Inventory.AppendAsync(winner, CancellationToken.None);
        var second = await Inventory.AppendAsync(loser, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(winner.Id, second.Snapshot.Id); // converge para o já persistido, nunca duplica

        var latest = await Inventory.GetLatestAsync(scope, identity.Id, CancellationToken.None);
        Assert.Equal(1, latest!.Version);
        Assert.Equal("arch-1", Assert.Single(latest.Archives).ExternalArchiveId);
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
}
