using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>Testes de domínio da fundação de connector EV (Slice 4C, Passo 1, AB-4C-001).</summary>
public sealed class Slice4cEvConnectorTests
{
    private static TenantId Tenant => new(Guid.NewGuid());

    private static ProjectId Project => new(Guid.NewGuid());

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    // ---- ConnectorPublicKeyThumbprint --------------------------------------------------------------

    [Fact]
    public void ThumbprintAccepts64LowercaseHexCharacters()
    {
        var thumbprint = new ConnectorPublicKeyThumbprint(new string('a', 64));
        Assert.Equal(64, thumbprint.Value.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase rejeitado
    public void ThumbprintRejectsInvalidFormat(string value)
    {
        Assert.Throws<ArgumentException>(() => new ConnectorPublicKeyThumbprint(value));
    }

    // ---- EnrollmentToken (§15.1, AB-4C-001 critério 1) ---------------------------------------------

    [Fact]
    public void IssueRejectsWindowLongerThanMaxTtl()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t1"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IssueRejectsNonPositiveWindow()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t2"), "operator@example.com",
            CorrelationId.New(), now, now));
    }

    [Fact]
    public void IssueAcceptsExactlyTheMaxTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t3"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);

        Assert.Equal(EnrollmentTokenStatus.Issued, token.Status);
    }

    [Fact]
    public void ConsumeHappyPathTransitionsToConsumedAndRecordsTheConnector()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t4"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);
        var connector = ConnectorId.New();

        var consumed = token.Consume(connector, now + TimeSpan.FromMinutes(1));

        Assert.Equal(EnrollmentTokenStatus.Consumed, consumed.Status);
        Assert.Equal(connector, consumed.ConsumedByConnector);
        Assert.NotNull(consumed.ConsumedAtUtc);
    }

    [Fact]
    public void ConsumeAfterExpiryFailsClosedEvenIfStillFormallyIssued()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t5"), "operator@example.com",
            CorrelationId.New(), now, now + TimeSpan.FromMinutes(15));

        Assert.Throws<EnrollmentTokenExpiredException>(
            () => token.Consume(ConnectorId.New(), now + TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ConsumeTwiceIsARejectedReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t6"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);

        var consumed = token.Consume(ConnectorId.New(), now);

        Assert.Throws<EnrollmentTokenNotConsumableException>(() => consumed.Consume(ConnectorId.New(), now));
    }

    [Fact]
    public void ConsumeARevokedTokenFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t7"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);

        var revoked = token.Revoke();

        Assert.Throws<EnrollmentTokenNotConsumableException>(() => revoked.Consume(ConnectorId.New(), now));
    }

    [Fact]
    public void RevokeAConsumedTokenFailsClosedTheEffectAlreadyHappened()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t8"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);
        var consumed = token.Consume(ConnectorId.New(), now);

        Assert.Throws<EnrollmentTokenNotConsumableException>(() => consumed.Revoke());
    }

    [Fact]
    public void RevokingAnAlreadyRevokedTokenIsIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var token = EnrollmentToken.Issue(
            EnrollmentTokenId.New(), Tenant, Project, Hash("t9"), "operator@example.com",
            CorrelationId.New(), now, now + EnrollmentToken.MaxTtl);

        var revokedOnce = token.Revoke();
        var revokedTwice = revokedOnce.Revoke();

        Assert.Equal(EnrollmentTokenStatus.Revoked, revokedTwice.Status);
    }

    // ---- ConnectorIdentity (§15.1, AB-4C-001 critério 2) -------------------------------------------

    [Fact]
    public void RegisterCreatesAnActiveIdentityWithSanitizedFields()
    {
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), Tenant, Project, new ConnectorPublicKeyThumbprint(new string('b', 64)),
            "  host01.contoso.local  ", "Site-A", "1.0.0", EnrollmentTokenId.New(), DateTimeOffset.UtcNow);

        Assert.True(identity.IsActive);
        Assert.Equal("host01.contoso.local", identity.Hostname);
    }

    [Fact]
    public void ReRegisterConvergesFieldsWithoutChangingTheOpaqueIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), Tenant, Project, new ConnectorPublicKeyThumbprint(new string('c', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);

        var reRegistered = identity.ReRegister("host01-renamed", "Site-B", "1.1.0", EnrollmentTokenId.New(), now.AddDays(1));

        Assert.Equal(identity.Id, reRegistered.Id);
        Assert.Equal("host01-renamed", reRegistered.Hostname);
        Assert.Equal(identity.RegisteredAtUtc, reRegistered.RegisteredAtUtc);
    }

    [Fact]
    public void ReRegisterARevokedConnectorFailsClosedNeverReactivatesSilently()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = ConnectorIdentity.Register(
            ConnectorId.New(), Tenant, Project, new ConnectorPublicKeyThumbprint(new string('d', 64)),
            "host01", "Site-A", "1.0.0", EnrollmentTokenId.New(), now);
        var revoked = identity.Revoke(now.AddMinutes(1));

        Assert.Throws<ConnectorRevokedException>(
            () => revoked.ReRegister("host01", "Site-A", "1.0.1", EnrollmentTokenId.New(), now.AddDays(1)));
    }

    // ---- ConnectorSupportMatrix / ConnectorCapabilityHandshake (AB-4C-001 critérios 4/5) -----------

    [Theory]
    [InlineData("15.0.1", ConnectorSupportLevel.Compatible)]
    [InlineData("14.2.2", ConnectorSupportLevel.Compatible)]
    [InlineData("12.1.0", ConnectorSupportLevel.Compatible)]
    [InlineData("", ConnectorSupportLevel.Unknown)]
    [InlineData("  ", ConnectorSupportLevel.Unknown)]
    [InlineData("99.0.0", ConnectorSupportLevel.Unknown)]
    [InlineData("not-a-version", ConnectorSupportLevel.Unknown)]
    public void SupportMatrixEvaluatesKnownFamiliesConservatively(string version, ConnectorSupportLevel expected)
    {
        Assert.Equal(expected, ConnectorSupportMatrix.Evaluate(version));
    }

    [Fact]
    public void NoFamilyIsCertifiedByDefaultHonestyRule()
    {
        // Nenhuma versão observável hoje produz Certified — certificação exige evidência de laboratório
        // registrada explicitamente na matriz, nunca inferida da string de versão sozinha (ADR-0013).
        string[] observedVersions = ["15.0.1", "14.2.2", "13.0.0", "12.1.0", "12.0.5", "11.0.0", "10.0.0"];
        foreach (var version in observedVersions)
        {
            Assert.NotEqual(ConnectorSupportLevel.Certified, ConnectorSupportMatrix.Evaluate(version));
        }
    }

    [Fact]
    public void HandshakeWithUnknownVersionBlocksExportWithSchemaUnknownDiagnostic()
    {
        var handshake = ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), ConnectorId.New(), Tenant, Project,
            "99.9.9", powerShellSnapinAvailable: true, CorrelationId.New(), DateTimeOffset.UtcNow);

        Assert.False(handshake.ExportCapable);
        Assert.Equal("SCHEMA_VERSION_UNKNOWN", handshake.BlockingReason);
    }

    [Fact]
    public void HandshakeWithCompatibleFamilyButNoSnapinBlocksExport()
    {
        var handshake = ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), ConnectorId.New(), Tenant, Project,
            "14.2.2", powerShellSnapinAvailable: false, CorrelationId.New(), DateTimeOffset.UtcNow);

        Assert.False(handshake.ExportCapable);
        Assert.Equal("POWERSHELL_SNAPIN_UNAVAILABLE", handshake.BlockingReason);
    }

    [Fact]
    public void HandshakeWithCompatibleFamilyAndSnapinStillBlocksExportUntilCertified()
    {
        var handshake = ConnectorCapabilityHandshake.Evaluate(
            CapabilityHandshakeId.New(), ConnectorId.New(), Tenant, Project,
            "14.2.2", powerShellSnapinAvailable: true, CorrelationId.New(), DateTimeOffset.UtcNow);

        Assert.False(handshake.ExportCapable);
        Assert.Equal("FAMILY_NOT_CERTIFIED", handshake.BlockingReason);
    }

    // ---- InventorySnapshot (AB-4C-001 critérios 6/7/8) ---------------------------------------------

    private static InventoryArchiveRecord Archive(string externalId, string type = "Mailbox") =>
        new(externalId, type, "VaultStore-1", InventoryArchiveStatus.Active, ["EV_POWERSHELL_AVAILABLE"]);

    [Fact]
    public void CreateRejectsDuplicateExternalArchiveIdsBeforeHashing()
    {
        Assert.Throws<DuplicateInventoryArchiveException>(() => InventorySnapshot.Create(
            InventorySnapshotId.New(), ConnectorId.New(), Tenant, Project, version: 1,
            [Archive("arch-1"), Archive("arch-1")], CorrelationId.New(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CreateRejectsNonPositiveVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InventorySnapshot.Create(
            InventorySnapshotId.New(), ConnectorId.New(), Tenant, Project, version: 0,
            [Archive("arch-1")], CorrelationId.New(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void HashIsDeterministicRegardlessOfInputOrder()
    {
        var connector = ConnectorId.New();
        var correlation = CorrelationId.New();
        var now = DateTimeOffset.UtcNow;

        var a = InventorySnapshot.Create(
            InventorySnapshotId.New(), connector, Tenant, Project, 1,
            [Archive("arch-1"), Archive("arch-2")], correlation, now);
        var b = InventorySnapshot.Create(
            InventorySnapshotId.New(), connector, Tenant, Project, 1,
            [Archive("arch-2"), Archive("arch-1")], correlation, now);

        Assert.Equal(a.SnapshotHash, b.SnapshotHash);
    }

    [Fact]
    public void OneFieldChangeInAnArchiveChangesTheSnapshotHash()
    {
        var connector = ConnectorId.New();
        var correlation = CorrelationId.New();
        var now = DateTimeOffset.UtcNow;

        var a = InventorySnapshot.Create(
            InventorySnapshotId.New(), connector, Tenant, Project, 1, [Archive("arch-1")], correlation, now);
        var b = InventorySnapshot.Create(
            InventorySnapshotId.New(), connector, Tenant, Project, 1,
            [new InventoryArchiveRecord("arch-1", "Journal", "VaultStore-1", InventoryArchiveStatus.Active, ["EV_POWERSHELL_AVAILABLE"])],
            correlation, now);

        Assert.NotEqual(a.SnapshotHash, b.SnapshotHash);
    }

    [Fact]
    public void EmptyArchiveListProducesAStableEmptySnapshotHash()
    {
        var snapshot = InventorySnapshot.Create(
            InventorySnapshotId.New(), ConnectorId.New(), Tenant, Project, 1, [], CorrelationId.New(), DateTimeOffset.UtcNow);

        Assert.Empty(snapshot.Archives);
        Assert.Equal(64, snapshot.SnapshotHash.Value.Length);
    }

    // ---- ExportRequestPolicy / ExportRequest (STOP-THE-LINE — nunca executado, AB-4C-001 crit. 10) -

    [Theory]
    [InlineData(ExportRequestPolicy.MinPstSizeMb - 1, ExportRequestPolicy.DefaultMaxThreads)]
    [InlineData(ExportRequestPolicy.MaxPstSizeMbCeiling + 1, ExportRequestPolicy.DefaultMaxThreads)]
    [InlineData(ExportRequestPolicy.DefaultMaxPstSizeMb, ExportRequestPolicy.MinThreads - 1)]
    [InlineData(ExportRequestPolicy.DefaultMaxPstSizeMb, ExportRequestPolicy.MaxThreadsCeiling + 1)]
    public void CreateRejectsLimitsOutsideTheDocumentedEnvelope(int maxPstSizeMb, int maxThreads)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportRequestPolicy.Create(maxPstSizeMb, maxThreads));
    }

    [Theory]
    [InlineData(ExportRequestPolicy.MinPstSizeMb, ExportRequestPolicy.MinThreads)]
    [InlineData(ExportRequestPolicy.MaxPstSizeMbCeiling, ExportRequestPolicy.MaxThreadsCeiling)]
    public void CreateAcceptsTheDocumentedBoundaryValues(int maxPstSizeMb, int maxThreads)
    {
        var policy = ExportRequestPolicy.Create(maxPstSizeMb, maxThreads);
        Assert.Equal(maxPstSizeMb, policy.MaxPstSizeMb);
        Assert.Equal(maxThreads, policy.MaxThreads);
    }

    [Fact]
    public void DefaultPolicyMatchesTheRunbookDefaults()
    {
        Assert.Equal(18432, ExportRequestPolicy.Default.MaxPstSizeMb);
        Assert.Equal(8, ExportRequestPolicy.Default.MaxThreads);
    }

    [Fact]
    public void ExportRequestSanitizesTheExternalArchiveId()
    {
        var request = new ExportRequest(ConnectorId.New(), "  arch-1  ", ExportRequestPolicy.Default, CorrelationId.New());
        Assert.Equal("arch-1", request.ExternalArchiveId);
    }
}
