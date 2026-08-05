using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Slice 3 — impressões digitais COMPLETAS que identificam uma reserva de descoberta. O
/// <see cref="EvDiscoveryConfiguration"/> (ConfigurationHash) inclui ambiente, versão/hash de configuração
/// do projeto, versão da política, capacidades exigidas ORDENADAS, limites e versões de esquema/catálogo;
/// o <see cref="EvDiscoverySemanticFingerprint"/> inclui identidade integral do ambiente, versão observada,
/// capacidades, assinatura normalizada, avaliações de adapter, achados, status e código. Qualquer diferença
/// semântica muda o hash — de modo que uma reserva incompatível NUNCA é reutilizada.
/// </summary>
public sealed class Slice3FingerprintTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly EvEnvironmentId Env = new(Guid.NewGuid());
    private static readonly Sha256Hash ProjHash = new(new string('1', 64));
    private const int ProjVer = 3;

    // ---- ConfigurationHash ---------------------------------------------------------------------------

    private static Sha256Hash ConfigHash(
        EvEnvironmentId? env = null, EvDiscoveryPolicy? policy = null, int? projVer = null, Sha256Hash? projHash = null) =>
        EvDiscoveryConfiguration.For(env ?? Env, policy ?? EvDiscoveryPolicy.Default, projVer ?? ProjVer, projHash ?? ProjHash).ComputeHash();

    [Fact]
    public void ConfigurationHashIsDeterministicForEqualInputs() =>
        Assert.Equal(ConfigHash().Value, ConfigHash().Value);

    [Fact]
    public void ConfigurationHashChangesWithEnvironment() =>
        Assert.NotEqual(ConfigHash().Value, ConfigHash(env: new EvEnvironmentId(Guid.NewGuid())).Value);

    [Fact]
    public void ConfigurationHashChangesWithProjectContext()
    {
        Assert.NotEqual(ConfigHash().Value, ConfigHash(projVer: ProjVer + 1).Value);
        Assert.NotEqual(ConfigHash().Value, ConfigHash(projHash: new Sha256Hash(new string('2', 64))).Value);
    }

    [Fact]
    public void ConfigurationHashChangesWithPolicyVersion()
    {
        var other = EvDiscoveryPolicy.Default with { PolicyVersion = EvDiscoveryPolicy.Default.PolicyVersion + 1 };
        Assert.NotEqual(ConfigHash().Value, ConfigHash(policy: other).Value);
    }

    [Fact]
    public void ConfigurationHashChangesWithRequiredForReadySet()
    {
        var extra = new List<EvCapabilityCode>(EvDiscoveryPolicy.Default.RequiredForReady)
        {
            new(EvCapabilityCodes.EvStagingPathAccess),
        };
        var other = EvDiscoveryPolicy.Default with { RequiredForReady = extra };
        Assert.NotEqual(ConfigHash().Value, ConfigHash(policy: other).Value);
    }

    [Fact]
    public void ConfigurationHashIsIndependentOfRequiredForReadyOrder()
    {
        var reversed = EvDiscoveryPolicy.Default.RequiredForReady.Reverse().ToList();
        var other = EvDiscoveryPolicy.Default with { RequiredForReady = reversed };
        // A ordem das capacidades exigidas é normalizada (ordenada) antes do hash.
        Assert.Equal(ConfigHash().Value, ConfigHash(policy: other).Value);
    }

    [Fact]
    public void ConfigurationHashChangesWithProbeTimeoutAndMaxOutputBytes()
    {
        var timeout = EvDiscoveryPolicy.Default with { ProbeTimeout = EvDiscoveryPolicy.Default.ProbeTimeout + TimeSpan.FromSeconds(1) };
        var bytes = EvDiscoveryPolicy.Default with { MaxOutputBytes = EvDiscoveryPolicy.Default.MaxOutputBytes + 1 };
        Assert.NotEqual(ConfigHash().Value, ConfigHash(policy: timeout).Value);
        Assert.NotEqual(ConfigHash().Value, ConfigHash(policy: bytes).Value);
    }

    // ---- SemanticEvidenceHash ------------------------------------------------------------------------

    private static EvExportSignature Signature(IEnumerable<string> parameters) => EvExportSignature.Create(
        "Export-EVArchive", "Symantec.EnterpriseVault.PowerShell", "15.1", "Cmdlet",
        parameters, ["ArchiveId", "OutputDirectory"], ["Default"], Now);

    // Mesma FORMA (SignatureHash idêntico, pois não depende da versão textual); só ObservedVersion difere.
    private static EvExportSignature SignatureVer(string observedVersion) => EvExportSignature.Create(
        "Export-EVArchive", "Symantec.EnterpriseVault.PowerShell", observedVersion, "Cmdlet",
        OfficialParameters, ["ArchiveId", "OutputDirectory"], ["Default"], Now);

    private static readonly string[] OfficialParameters =
        ["ArchiveId", "OutputDirectory", "SearchString", "Format", "MaxThreads", "Retry", "MaxPSTSizeMB"];

    private static EvDiscoveryObservation Observe(string observedVersion, EvExportSignature? signature, bool permissions)
    {
        var identity = new EvEnvironmentIdentity(Env, "site", "dir", observedVersion, observedVersion, "PowerShell", Now);
        var probes = new List<EvProbeResult>
        {
            P(EvCapabilityCodes.EvPowershellAvailable, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvModuleAvailable, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvSnapinAvailable, CapabilityAvailability.Unavailable),
            P(EvCapabilityCodes.EvDirectoryConnectivity, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvSiteDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvServerDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvVaultStoreDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvArchiveDiscovery, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvExportCmdletAvailable, signature is null ? CapabilityAvailability.Unavailable : CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvStagingPathAccess, CapabilityAvailability.Available),
            P(EvCapabilityCodes.EvRequiredPermissions, permissions ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
        };
        return new EvDiscoveryObservation(identity, probes, signature, []);
    }

    private static EvProbeResult P(string code, CapabilityAvailability availability) =>
        new(new EvCapabilityCode(code), availability, "ref", availability == CapabilityAvailability.Available ? null : "n/a");

    private static Sha256Hash SemanticHash(EvDiscoveryObservation observation)
    {
        var selection = new AdapterCompatibilityEvaluator([new EvExportDocumented151Adapter()]).Select(observation, EvDiscoveryPolicy.Default);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, selection, EvDiscoveryPolicy.Default, Now, Now);
        return EvDiscoverySemanticFingerprint.Compute(result);
    }

    [Fact]
    public void SemanticFingerprintIsDeterministicForEqualObservations()
    {
        var a = SemanticHash(Observe("15.1", Signature(OfficialParameters), permissions: true));
        var b = SemanticHash(Observe("15.1", Signature(OfficialParameters), permissions: true));
        Assert.Equal(a.Value, b.Value);
    }

    [Fact]
    public void SemanticFingerprintChangesWithSignature()
    {
        var baseline = SemanticHash(Observe("15.1", Signature(OfficialParameters), permissions: true));
        var noFiltering = SemanticHash(Observe("15.1", Signature(["ArchiveId", "OutputDirectory", "Format", "MaxPSTSizeMB"]), permissions: true));
        // Mesmas capacidades exigidas comprovadas (Ready), porém assinatura diferente ⇒ hash semântico diferente.
        Assert.NotEqual(baseline.Value, noFiltering.Value);
    }

    [Fact]
    public void SemanticFingerprintChangesWithObservedVersion()
    {
        var baseline = SemanticHash(Observe("15.1", Signature(OfficialParameters), permissions: true));
        var other = SemanticHash(Observe("15.2", Signature(OfficialParameters), permissions: true));
        Assert.NotEqual(baseline.Value, other.Value);
    }

    [Fact]
    public void SemanticFingerprintChangesWithSignatureObservedVersionAloneWithEnvironmentFixed()
    {
        // Ambiente IDÊNTICO (ObservedVersion/ProductVersion "15.1") e a MESMA forma de assinatura
        // (SignatureHash igual): a ÚNICA diferença é Signature.ObservedVersion. Prova que o campo entra
        // no hash semântico — ao contrário de SemanticFingerprintChangesWithObservedVersion, que varia o
        // ambiente e mantém a assinatura fixa em 15.1.
        var sig151 = SignatureVer("15.1");
        var sig152 = SignatureVer("15.2");
        Assert.Equal(sig151.SignatureHash.Value, sig152.SignatureHash.Value); // forma idêntica; só a versão textual difere

        var baseline = SemanticHash(Observe("15.1", sig151, permissions: true));
        var other = SemanticHash(Observe("15.1", sig152, permissions: true));
        Assert.NotEqual(baseline.Value, other.Value);
    }

    [Fact]
    public void SemanticFingerprintChangesWithFindingsAndStatus()
    {
        var ready = SemanticHash(Observe("15.1", Signature(OfficialParameters), permissions: true));
        var blocked = SemanticHash(Observe("15.1", Signature(OfficialParameters), permissions: false));
        Assert.NotEqual(ready.Value, blocked.Value);
    }
}
