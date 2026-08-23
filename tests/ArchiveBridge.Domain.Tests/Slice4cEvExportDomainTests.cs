using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// Testes de domínio da fundação de execução de export EV (Slice 4C, Passo 2, AB-4C-005): política efetiva
/// (item 3), identidade canônica do pedido (item 5), argumentos tipados do comando (item 7), manifesto
/// canônico (item 12) e classificação de itens oversized (item 14).
/// </summary>
public sealed class Slice4cEvExportDomainTests
{
    private static TenantId Tenant => new(Guid.NewGuid());

    private static ProjectId Project => new(Guid.NewGuid());

    // ---- MaxThreads boundaries (item 3; MaxPstSizeMb boundaries já cobertos por Slice4cEvConnectorTests) --

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void CreateRejectsMaxThreadsOutsideTheDocumentedEnvelope(int maxThreads)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportRequestPolicy.Create(ExportRequestPolicy.DefaultMaxPstSizeMb, maxThreads));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void CreateAcceptsMaxThreadsWithinTheDocumentedEnvelope(int maxThreads)
    {
        var policy = ExportRequestPolicy.Create(ExportRequestPolicy.DefaultMaxPstSizeMb, maxThreads);
        Assert.Equal(maxThreads, policy.MaxThreads);
    }

    // ---- EvExportPolicy: envelope EFETIVO nunca pode exceder o teto documentado do cmdlet (item 3) -------

    [Fact]
    public void EffectivePolicyCannotExceedTheDocumentedCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EvExportPolicy.Create(
            1, ExportRequestPolicy.MaxThreadsCeiling + 1, ExportRequestPolicy.DefaultMaxPstSizeMb, TimeSpan.FromHours(1), 1024));
    }

    [Fact]
    public void EffectivePolicyRejectsARequestAboveItsOwnNarrowerCap()
    {
        // Um deployment pode restringir o envelope ABAIXO do teto do cmdlet (ex.: MaxThreads efetivo = 4).
        var narrower = EvExportPolicy.Create(1, effectiveMaxThreads: 4, ExportRequestPolicy.DefaultMaxPstSizeMb, TimeSpan.FromHours(1), 1024);
        var requested = ExportRequestPolicy.Create(ExportRequestPolicy.DefaultMaxPstSizeMb, maxThreads: 8);

        Assert.Throws<EvExportValidationException>(() => narrower.EnsureWithinEffectiveEnvelope(requested));
    }

    [Fact]
    public void EffectivePolicyAcceptsARequestAtExactlyItsCap()
    {
        var narrower = EvExportPolicy.Create(1, effectiveMaxThreads: 4, ExportRequestPolicy.DefaultMaxPstSizeMb, TimeSpan.FromHours(1), 1024);
        var requested = ExportRequestPolicy.Create(ExportRequestPolicy.DefaultMaxPstSizeMb, maxThreads: 4);

        narrower.EnsureWithinEffectiveEnvelope(requested); // não lança
    }

    [Fact]
    public void DefaultEffectivePolicyMatchesTheDocumentedCeiling()
    {
        Assert.Equal(ExportRequestPolicy.MaxThreadsCeiling, EvExportPolicy.Default.EffectiveMaxThreads);
        Assert.Equal(ExportRequestPolicy.MaxPstSizeMbCeiling, EvExportPolicy.Default.EffectiveMaxPstSizeMb);
    }

    // ---- EvExportRequestIdentity: identidade canônica (item 5) ---------------------------------------

    [Fact]
    public void IdenticalInputsProduceTheSameCanonicalIdentity()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();
        var policy = ExportRequestPolicy.Default;

        var a = EvExportRequestIdentity.Compute(tenant, project, connector, "arch-1", policy);
        var b = EvExportRequestIdentity.Compute(tenant, project, connector, "  arch-1  ", policy);

        Assert.Equal(a.Value, b.Value);
        Assert.Equal(a.ToIdempotencyKey(), b.ToIdempotencyKey());
    }

    [Fact]
    public void ADifferentPolicyProducesADifferentCanonicalIdentity()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();

        var a = EvExportRequestIdentity.Compute(tenant, project, connector, "arch-1", ExportRequestPolicy.Default);
        var b = EvExportRequestIdentity.Compute(
            tenant, project, connector, "arch-1", ExportRequestPolicy.Create(ExportRequestPolicy.DefaultMaxPstSizeMb, maxThreads: 4));

        Assert.NotEqual(a.Value, b.Value);
        Assert.NotEqual(a.ToIdempotencyKey(), b.ToIdempotencyKey());
    }

    [Fact]
    public void ADifferentArchiveProducesADifferentCanonicalIdentity()
    {
        var tenant = Tenant;
        var project = Project;
        var connector = ConnectorId.New();

        var a = EvExportRequestIdentity.Compute(tenant, project, connector, "arch-1", ExportRequestPolicy.Default);
        var b = EvExportRequestIdentity.Compute(tenant, project, connector, "arch-2", ExportRequestPolicy.Default);

        Assert.NotEqual(a.Value, b.Value);
    }

    // ---- EvExportCommandArguments (item 7): forma segura, nunca uma string de comando -----------------

    [Fact]
    public void CommandArgumentsAlwaysCarryPstFormatAndRetry()
    {
        var arguments = EvExportCommandArguments.Create("arch-1", "aa/bb/cc", ExportRequestPolicy.Default);

        Assert.Equal("PST", arguments.Format);
        Assert.True(arguments.Retry);
        Assert.Equal("arch-1", arguments.ExternalArchiveId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CommandArgumentsRejectEmptyArchiveId(string archiveId)
    {
        Assert.Throws<ArgumentException>(() => EvExportCommandArguments.Create(archiveId, "aa/bb", ExportRequestPolicy.Default));
    }

    // ---- EvExportManifest (item 12): determinístico, hash/tamanho, identidade por conteúdo ------------

    private static EvExportManifestEntry Entry(string hashSeed, long size) =>
        new("output.pst", size, DeterministicHash.Compute([hashSeed]));

    [Fact]
    public void ManifestHashIsDeterministicRegardlessOfEntryOrder()
    {
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var a = EvExportManifest.Create(request, attempt, "arch-1", [Entry("h1", 100), Entry("h2", 200)], "15.0", "1.0.0");
        var b = EvExportManifest.Create(request, attempt, "arch-1", [Entry("h2", 200), Entry("h1", 100)], "15.0", "1.0.0");

        Assert.Equal(a.ManifestHash, b.ManifestHash);
        Assert.Equal(a.Entries.Select(e => e.ContentSha256), b.Entries.Select(e => e.ContentSha256));
    }

    [Fact]
    public void ManifestEntryIdentityIsDerivedFromContentNeverFromFileName()
    {
        var hash = DeterministicHash.Compute(["same-content"]);
        var a = new EvExportManifestEntry("first-name.pst", 100, hash);
        var b = new EvExportManifestEntry("totally-different-name.pst", 100, hash);

        Assert.Equal(a.Id, b.Id); // mesmo conteúdo/tamanho ⇒ mesma identidade, apesar do nome divergente
    }

    [Fact]
    public void ManifestRejectsDuplicateContentIdentity()
    {
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var hash = DeterministicHash.Compute(["dup"]);
        var entries = new[] { new EvExportManifestEntry("a.pst", 100, hash), new EvExportManifestEntry("b.pst", 100, hash) };

        Assert.Throws<EvExportManifestValidationException>(
            () => EvExportManifest.Create(request, attempt, "arch-1", entries, "15.0", "1.0.0"));
    }

    [Fact]
    public void RehydrateFailsClosedWhenPersistedHashDoesNotMatchLoadedEntries()
    {
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var entries = new[] { Entry("h1", 100) };
        var forgedHash = DeterministicHash.Compute(["not-the-real-hash"]);

        Assert.Throws<EvExportManifestValidationException>(
            () => EvExportManifest.Rehydrate(request, attempt, "arch-1", entries, "15.0", "1.0.0", forgedHash));
    }

    [Fact]
    public void RehydrateOfAnUntamperedManifestRoundTripsExactly()
    {
        var request = ExportRequestId.New();
        var attempt = ExportAttemptId.New();
        var created = EvExportManifest.Create(request, attempt, "arch-1", [Entry("h1", 100)], "15.0", "1.0.0");

        var rehydrated = EvExportManifest.Rehydrate(
            request, attempt, "arch-1", created.Entries, created.EngineVersion, created.ConnectorVersion, created.ManifestHash);

        Assert.Equal(created.ManifestHash, rehydrated.ManifestHash);
    }

    // ---- EvOversizedNativeItem (item 14): classificação por limiar (§16.4) -----------------------------

    [Fact]
    public void ItemAtOrBelowPstThresholdIsNeverClassifiedAsOversized()
    {
        Assert.Null(EvOversizedNativeItem.Classify(EvOversizedNativeItem.PstItemThresholdBytes));
        Assert.Null(EvOversizedNativeItem.Classify(1));
    }

    [Fact]
    public void ItemJustAbovePstThresholdIsExceedsPstItemThreshold()
    {
        var classification = EvOversizedNativeItem.Classify(EvOversizedNativeItem.PstItemThresholdBytes + 1);
        Assert.Equal(EvOversizedNativeItemClassification.ExceedsPstItemThreshold, classification);
    }

    [Fact]
    public void ItemAtNativeThresholdIsStillExceedsPstItemThreshold()
    {
        var classification = EvOversizedNativeItem.Classify(EvOversizedNativeItem.NativeExportThresholdBytes);
        Assert.Equal(EvOversizedNativeItemClassification.ExceedsPstItemThreshold, classification);
    }

    [Fact]
    public void ItemAboveNativeThresholdIsExceedsNativeExportThreshold()
    {
        var classification = EvOversizedNativeItem.Classify(EvOversizedNativeItem.NativeExportThresholdBytes + 1);
        Assert.Equal(EvOversizedNativeItemClassification.ExceedsNativeExportThreshold, classification);
    }

    [Fact]
    public void OversizedItemConstructorRejectsASizeAtOrBelowTheThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvOversizedNativeItem(
            "item-ref-1", EvOversizedNativeItem.PstItemThresholdBytes, EvOversizedNativeItemClassification.ExceedsPstItemThreshold,
            null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OversizedItemNeverAcceptsAnEmptyExternalReference()
    {
        Assert.Throws<ArgumentException>(() => new EvOversizedNativeItem(
            "", EvOversizedNativeItem.PstItemThresholdBytes + 1, EvOversizedNativeItemClassification.ExceedsPstItemThreshold,
            null, DateTimeOffset.UtcNow));
    }
}
