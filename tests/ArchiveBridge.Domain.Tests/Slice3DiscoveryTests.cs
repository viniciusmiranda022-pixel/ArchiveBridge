using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>Slice 3 — máquina de estados, hashes determinísticos, códigos tipados e assinatura normalizada.</summary>
public sealed class Slice3DiscoveryStateAndHashTests
{
    [Theory]
    [InlineData(EvDiscoveryStatus.Pending, EvDiscoveryStatus.Discovering, true)]
    [InlineData(EvDiscoveryStatus.Discovering, EvDiscoveryStatus.Ready, true)]
    [InlineData(EvDiscoveryStatus.Discovering, EvDiscoveryStatus.Blocked, true)]
    [InlineData(EvDiscoveryStatus.Ready, EvDiscoveryStatus.Superseded, true)]
    [InlineData(EvDiscoveryStatus.Pending, EvDiscoveryStatus.Ready, false)]
    [InlineData(EvDiscoveryStatus.Ready, EvDiscoveryStatus.Blocked, false)]
    [InlineData(EvDiscoveryStatus.Superseded, EvDiscoveryStatus.Ready, false)]
    public void TransitionsAreFailClosed(EvDiscoveryStatus from, EvDiscoveryStatus to, bool allowed)
    {
        Assert.Equal(allowed, EvDiscoveryTransitions.CanTransition(from, to));
        if (!allowed)
        {
            Assert.Throws<InvalidEvDiscoveryTransitionException>(() => EvDiscoveryTransitions.EnsureCanTransition(from, to));
        }
    }

    [Fact]
    public void OneByteChangeInCapabilityChangesEvidenceHash()
    {
        var env = new EvEnvironmentId(Guid.NewGuid());
        var baseline = EvCapabilitySet.Create(env, new EvAdapterId("a"), 1, 1, [Capability(CapabilityAvailability.Available)], EvDiscoveryStatus.Ready);
        var mutated = EvCapabilitySet.Create(env, new EvAdapterId("a"), 1, 1, [Capability(CapabilityAvailability.Unavailable)], EvDiscoveryStatus.Ready);
        Assert.NotEqual(baseline.EvidenceHash.Value, mutated.EvidenceHash.Value);
        // Mesma entrada semântica ⇒ hash idêntico (determinístico).
        var repeat = EvCapabilitySet.Create(env, new EvAdapterId("a"), 1, 1, [Capability(CapabilityAvailability.Available)], EvDiscoveryStatus.Ready);
        Assert.Equal(baseline.EvidenceHash.Value, repeat.EvidenceHash.Value);
        Assert.Equal(baseline.ConfigurationHash.Value, repeat.ConfigurationHash.Value);
    }

    [Fact]
    public void AvailabilityOfMissingCapabilityIsIndeterminate()
    {
        var set = EvCapabilitySet.Create(new EvEnvironmentId(Guid.NewGuid()), null, null, 1, [], EvDiscoveryStatus.Blocked);
        Assert.Equal(CapabilityAvailability.Indeterminate, set.AvailabilityOf(EvCapabilityCodes.EvExportCmdletAvailable));
    }

    [Fact]
    public void UnknownCapabilityAndResultCodesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new EvCapabilityCode("EV_NOT_A_REAL_CAPABILITY"));
        Assert.Throws<ArgumentException>(() => new EvDiscoveryResultCode("NOT_A_REAL_CODE"));
        _ = new EvCapabilityCode(EvCapabilityCodes.EvExportPstSupported);
        _ = new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryCompleted);
    }

    [Fact]
    public void SignatureNormalizesAndHashesDeterministically()
    {
        var a = EvExportSignature.Create("Export-EVArchive", "Mod", "14", "Cmdlet",
            ["OutputPath", "ArchiveId"], ["ArchiveId"], ["Default"], DateTimeOffset.UnixEpoch);
        var b = EvExportSignature.Create("export-evarchive", "Mod", "14", "cmdlet",
            ["archiveid", "outputpath"], ["archiveid"], ["default"], DateTimeOffset.UnixEpoch.AddDays(1));
        // Case-insensitive + ordenação ⇒ mesma forma ⇒ mesmo hash, independentemente de ordem/caixa/timestamp.
        Assert.Equal(a.SignatureHash.Value, b.SignatureHash.Value);
        Assert.True(a.HasParameter("ArchiveId"));
        Assert.True(a.HasParameter("outputpath"));
        Assert.False(a.HasParameter("Filter"));

        var different = EvExportSignature.Create("Export-EVArchive", "Mod", "14", "Cmdlet",
            ["ExportPath", "ArchiveId"], ["ArchiveId"], ["Default"], DateTimeOffset.UnixEpoch);
        Assert.NotEqual(a.SignatureHash.Value, different.SignatureHash.Value);
    }

    private static EvCapability Capability(CapabilityAvailability availability) =>
        new(new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletAvailable), 1, availability, "ref", null, DateTimeOffset.UnixEpoch);
}

/// <summary>Slice 3 — seleção de adapter DETERMINÍSTICA e fail-closed (ausência/ambiguidade/bloqueio).</summary>
public sealed class Slice3AdapterSelectionTests
{
    [Fact]
    public void NoEvaluationsIsNotFound() =>
        Assert.Equal(AdapterSelectionOutcome.NotFound, AdapterSelectionPolicy.Select([]).Outcome);

    [Fact]
    public void SingleSupportedIsSelected()
    {
        var selection = AdapterSelectionPolicy.Select([Eval("a", AdapterCompatibility.Supported, 10)]);
        Assert.Equal(AdapterSelectionOutcome.Supported, selection.Outcome);
        Assert.Equal("a", selection.Selected!.AdapterId.Value);
    }

    [Fact]
    public void HigherPrecedenceWinsDeterministically()
    {
        var selection = AdapterSelectionPolicy.Select(
            [Eval("low", AdapterCompatibility.Supported, 5), Eval("high", AdapterCompatibility.Supported, 20)]);
        Assert.Equal(AdapterSelectionOutcome.Supported, selection.Outcome);
        Assert.Equal("high", selection.Selected!.AdapterId.Value);
    }

    [Fact]
    public void TiedPrecedenceAmongSupportedIsAmbiguous() =>
        Assert.Equal(AdapterSelectionOutcome.Ambiguous, AdapterSelectionPolicy.Select(
            [Eval("a", AdapterCompatibility.Supported, 10), Eval("b", AdapterCompatibility.Supported, 10)]).Outcome);

    [Fact]
    public void AllUnsupportedIsUnsupportedAndBlockedIsBlocked()
    {
        Assert.Equal(AdapterSelectionOutcome.Unsupported, AdapterSelectionPolicy.Select(
            [Eval("a", AdapterCompatibility.Unsupported, 1)]).Outcome);
        Assert.Equal(AdapterSelectionOutcome.Blocked, AdapterSelectionPolicy.Select(
            [Eval("a", AdapterCompatibility.Unsupported, 1), Eval("b", AdapterCompatibility.Blocked, 1)]).Outcome);
    }

    private static EvAdapterEvaluation Eval(string id, AdapterCompatibility compatibility, int precedence) =>
        new(new EvAdapterId(id), 1, compatibility, [], [], [], precedence);
}
