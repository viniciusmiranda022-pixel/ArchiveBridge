using System.Text.Json;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Descoberta READ-ONLY de capacidades via PowerShell CONTROLADO (allowlist). Executa um pequeno conjunto
/// de sondas — um snapshot de ambiente e a DESCOBERTA DO COMANDO de exportação (<c>Get-Command</c>) — e
/// interpreta as saídas JSON em observações FACTUAIS por capacidade. NUNCA executa exportação, nunca
/// modifica o Enterprise Vault. Uma saída malformada ⇒ achado <c>EV_DISCOVERY_OUTPUT_INVALID</c>; um
/// timeout ⇒ <c>EV_DISCOVERY_TIMEOUT</c> (fatais para a avaliação). A interpretação de SUPORTE por versão
/// é dos adapters — aqui só se reportam fatos.
/// </summary>
public sealed class PowerShellEvCapabilityDiscovery(IEvPowerShellExecutor executor, IClock clock) : IEvCapabilityDiscovery
{
    /// <summary>Comando de snapshot read-only do ambiente (allowlisted). Não modifica nada.</summary>
    public const string SnapshotCommand = "Get-EvEnvironmentSnapshot";

    /// <summary>Comando de descoberta de comando (allowlisted). Descobre o cmdlet de exportação sem executá-lo.</summary>
    public const string GetCommandCommand = "Get-Command";

    /// <summary>Nome do cmdlet de exportação cuja DISPONIBILIDADE é descoberta (nunca executado).</summary>
    public const string ExportCmdletName = "Export-EVArchive";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IEvPowerShellExecutor _executor = executor;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<EvDiscoveryObservation> ProbeAsync(
        EvEnvironmentDescriptor environment, EvDiscoveryPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(policy);
        var now = _clock.UtcNow;
        var findings = new List<EvDiscoveryFinding>();

        var snapshotResult = await _executor.InvokeAsync(
            new EvPowerShellRequest(SnapshotCommand,
                [new EvPowerShellParameter("DirectoryServer", environment.DirectoryServer), new EvPowerShellParameter("SiteName", environment.SiteName)],
                policy.ProbeTimeout),
            cancellationToken).ConfigureAwait(false);

        if (snapshotResult.TimedOut)
        {
            return Fatal(environment, EvDiscoveryResultCodes.DiscoveryTimeout, "Timeout na sonda de snapshot.", now);
        }

        var snapshot = TryParse<SnapshotOutput>(snapshotResult.StandardOutput);
        if (snapshot is null)
        {
            return Fatal(environment, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "Saída de snapshot inválida.", now);
        }

        var commandResult = await _executor.InvokeAsync(
            new EvPowerShellRequest(GetCommandCommand, [new EvPowerShellParameter("Name", ExportCmdletName)], policy.ProbeTimeout),
            cancellationToken).ConfigureAwait(false);
        if (commandResult.TimedOut)
        {
            return Fatal(environment, EvDiscoveryResultCodes.DiscoveryTimeout, "Timeout na descoberta do cmdlet.", now);
        }

        var command = TryParse<CommandOutput>(commandResult.StandardOutput);
        if (command is null)
        {
            return Fatal(environment, EvDiscoveryResultCodes.DiscoveryOutputInvalid, "Saída de Get-Command inválida.", now);
        }

        var identity = new EvEnvironmentIdentity(
            environment.EnvironmentId, environment.SiteName, environment.DirectoryServer,
            snapshot.ObservedVersion ?? string.Empty, snapshot.ProductVersion ?? string.Empty,
            snapshot.DiscoverySource ?? "PowerShell", now);

        var signature = command.Available && command.Signature is { } sig
            ? EvExportSignature.Create(
                sig.CommandName ?? ExportCmdletName, sig.ModuleSource ?? string.Empty, sig.Version ?? string.Empty,
                sig.CommandType ?? "Cmdlet", sig.Parameters ?? [], sig.Mandatory ?? [], sig.ParameterSets ?? [], now)
            : null;

        var probes = new List<EvProbeResult>
        {
            Probe(EvCapabilityCodes.EvPowershellAvailable, snapshot.PowershellAvailable, snapshot.PermissionDenied, "snapshot:powershell", now),
            Probe(EvCapabilityCodes.EvModuleAvailable, snapshot.ModuleAvailable, snapshot.PermissionDenied, "snapshot:module", now),
            Probe(EvCapabilityCodes.EvSnapinAvailable, snapshot.SnapinAvailable, snapshot.PermissionDenied, "snapshot:snapin", now),
            Probe(EvCapabilityCodes.EvDirectoryConnectivity, snapshot.DirectoryConnectivity, snapshot.PermissionDenied, "snapshot:directory", now),
            Probe(EvCapabilityCodes.EvSiteDiscovery, snapshot.Sites > 0, snapshot.PermissionDenied, "snapshot:sites", now),
            Probe(EvCapabilityCodes.EvServerDiscovery, snapshot.Servers > 0, snapshot.PermissionDenied, "snapshot:servers", now),
            Probe(EvCapabilityCodes.EvVaultStoreDiscovery, snapshot.VaultStores > 0, snapshot.PermissionDenied, "snapshot:vaultstores", now),
            Probe(EvCapabilityCodes.EvArchiveDiscovery, snapshot.Archives > 0, snapshot.PermissionDenied, "snapshot:archives", now),
            Probe(EvCapabilityCodes.EvExportCmdletAvailable, command.Available, denied: false, "getcommand:export", now),
            Probe(EvCapabilityCodes.EvStagingPathAccess, snapshot.StagingPathAccess, snapshot.PermissionDenied, "snapshot:staging", now),
            Probe(EvCapabilityCodes.EvRequiredPermissions, snapshot.RequiredPermissions, snapshot.PermissionDenied, "snapshot:permissions", now),
        };

        return new EvDiscoveryObservation(identity, probes, signature, findings);
    }

    private static EvDiscoveryObservation Fatal(EvEnvironmentDescriptor environment, string resultCode, string reason, DateTimeOffset now)
    {
        var identity = new EvEnvironmentIdentity(
            environment.EnvironmentId, environment.SiteName, environment.DirectoryServer, string.Empty, string.Empty, "PowerShell", now);
        var finding = new EvDiscoveryFinding(new EvDiscoveryResultCode(resultCode), CapabilityCode: null, reason, now);
        return new EvDiscoveryObservation(identity, [], ExportSignature: null, [finding]);
    }

    private static EvProbeResult Probe(string code, bool present, bool denied, string evidenceReference, DateTimeOffset now)
    {
        var availability = denied
            ? CapabilityAvailability.PermissionDenied
            : present ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable;
        var reason = availability == CapabilityAvailability.Available ? null : $"Sonda não comprovou a capacidade ({availability}).";
        return new EvProbeResult(new EvCapabilityCode(code), availability, evidenceReference, reason);
    }

    private static T? TryParse<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SnapshotOutput(
        bool PowershellAvailable, bool ModuleAvailable, bool SnapinAvailable, bool DirectoryConnectivity,
        int Sites, int Servers, int VaultStores, int Archives, bool StagingPathAccess, bool RequiredPermissions,
        bool PermissionDenied, string? ObservedVersion, string? ProductVersion, string? DiscoverySource);

    private sealed record CommandOutput(bool Available, SignatureOutput? Signature);

    private sealed record SignatureOutput(
        string? CommandName, string? ModuleSource, string? Version, string? CommandType,
        string[]? Parameters, string[]? Mandatory, string[]? ParameterSets);
}
