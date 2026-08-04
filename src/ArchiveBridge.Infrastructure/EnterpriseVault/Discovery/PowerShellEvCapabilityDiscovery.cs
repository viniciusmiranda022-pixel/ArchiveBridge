using System.Text.Json;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Descoberta READ-ONLY de capacidades via sondas TIPADAS (<see cref="EvPowerShellProbeKind"/>) executadas
/// pelo <see cref="EvProbeExecutor"/> (fail-closed) sobre um <see cref="IEvPowerShellHost"/>. Cada sonda
/// produz seu PRÓPRIO resultado (status + evidência + código + categoria de erro): a falha/PermissionDenied
/// de uma sonda não colapsa as demais. Nenhuma sonda executa <c>Export-EVArchive</c> — apenas descobre seus
/// metadados. A interpretação de SUPORTE por versão é dos adapters; aqui só se reportam fatos.
/// </summary>
public sealed class PowerShellEvCapabilityDiscovery(IEvPowerShellHost host, IClock clock) : IEvCapabilityDiscovery
{
    private readonly EvProbeExecutor _executor = new(host);
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<EvDiscoveryObservation> ProbeAsync(
        EvEnvironmentDescriptor environment, EvDiscoveryPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(policy);
        var now = _clock.UtcNow;
        var args = new[] { new EvProbeArgument("DirectoryServer", environment.DirectoryServer), new EvProbeArgument("SiteName", environment.SiteName) };
        var probes = new List<EvProbeResult>();
        var findings = new List<EvDiscoveryFinding>();
        var anyPermissionDenied = false;

        async Task<EvProbeOutcome> Run(EvPowerShellProbeKind kind) =>
            await _executor.RunAsync(kind, args, policy.ProbeTimeout, policy.MaxOutputBytes, cancellationToken).ConfigureAwait(false);

        // PowerShell environment.
        var psEnv = await Run(EvPowerShellProbeKind.PowerShellEnvironment).ConfigureAwait(false);
        probes.Add(BoolCapability(EvCapabilityCodes.EvPowershellAvailable, psEnv, static d => TryGetString(d, "psVersion") is { Length: > 0 }, "probe:powershell", now, findings, ref anyPermissionDenied));

        var moduleOutcome = await Run(EvPowerShellProbeKind.AvailableEvModule).ConfigureAwait(false);
        probes.Add(BoolCapability(EvCapabilityCodes.EvModuleAvailable, moduleOutcome, static d => TryGetBool(d, "present") == true, "probe:module", now, findings, ref anyPermissionDenied));

        var snapinOutcome = await Run(EvPowerShellProbeKind.RegisteredEvSnapin).ConfigureAwait(false);
        probes.Add(BoolCapability(EvCapabilityCodes.EvSnapinAvailable, snapinOutcome, static d => TryGetBool(d, "present") == true, "probe:snapin", now, findings, ref anyPermissionDenied));

        var siteOutcome = await Run(EvPowerShellProbeKind.EvSite).ConfigureAwait(false);
        probes.Add(CountCapability(EvCapabilityCodes.EvSiteDiscovery, siteOutcome, "probe:site", now, findings, ref anyPermissionDenied));
        // Conectividade de directory: derivada do sucesso da sonda de sites (a consulta ao directory funcionou).
        probes.Add(DerivedCapability(EvCapabilityCodes.EvDirectoryConnectivity, siteOutcome, "probe:directory", now, findings, ref anyPermissionDenied));

        var serverOutcome = await Run(EvPowerShellProbeKind.EvServer).ConfigureAwait(false);
        probes.Add(CountCapability(EvCapabilityCodes.EvServerDiscovery, serverOutcome, "probe:server", now, findings, ref anyPermissionDenied));

        var vaultOutcome = await Run(EvPowerShellProbeKind.EvVaultStore).ConfigureAwait(false);
        probes.Add(CountCapability(EvCapabilityCodes.EvVaultStoreDiscovery, vaultOutcome, "probe:vaultstore", now, findings, ref anyPermissionDenied));

        var archiveOutcome = await Run(EvPowerShellProbeKind.EvArchive).ConfigureAwait(false);
        probes.Add(CountCapability(EvCapabilityCodes.EvArchiveDiscovery, archiveOutcome, "probe:archive", now, findings, ref anyPermissionDenied));

        var stagingOutcome = await Run(EvPowerShellProbeKind.StagingPathAccess).ConfigureAwait(false);
        probes.Add(BoolCapability(EvCapabilityCodes.EvStagingPathAccess, stagingOutcome, static d => TryGetBool(d, "accessible") == true, "probe:staging", now, findings, ref anyPermissionDenied));

        var metaOutcome = await Run(EvPowerShellProbeKind.ExportEvArchiveCommandMetadata).ConfigureAwait(false);
        var cmdletAvailable = metaOutcome.Ok && TryGetBool(metaOutcome.Data, "available") == true;
        probes.Add(BoolCapability(EvCapabilityCodes.EvExportCmdletAvailable, metaOutcome, static d => TryGetBool(d, "available") == true, "probe:export-metadata", now, findings, ref anyPermissionDenied));
        var signature = cmdletAvailable ? TryReadSignature(metaOutcome.Data, now) : null;

        // Permissões: derivada — Available apenas se NENHUMA sonda reportou PermissionDenied.
        probes.Add(new EvProbeResult(
            new EvCapabilityCode(EvCapabilityCodes.EvRequiredPermissions),
            anyPermissionDenied ? CapabilityAvailability.PermissionDenied : CapabilityAvailability.Available,
            "probe:permissions",
            anyPermissionDenied ? "Uma ou mais sondas foram barradas por permissão." : null,
            anyPermissionDenied ? new EvDiscoveryResultCode(EvDiscoveryResultCodes.PermissionInsufficient) : null,
            anyPermissionDenied ? EvErrorCategory.PermissionDenied : EvErrorCategory.None));

        var identity = new EvEnvironmentIdentity(
            environment.EnvironmentId, environment.SiteName, environment.DirectoryServer,
            metaOutcome.Ok ? TryGetString(metaOutcome.Data, "observedVersion") ?? string.Empty : string.Empty,
            metaOutcome.Ok ? TryGetString(metaOutcome.Data, "productVersion") ?? string.Empty : string.Empty,
            "PowerShell", now);

        return new EvDiscoveryObservation(identity, probes, signature, findings);
    }

    private static EvProbeResult BoolCapability(
        string code, EvProbeOutcome outcome, Func<JsonElement, bool> present, string evidence, DateTimeOffset now,
        List<EvDiscoveryFinding> findings, ref bool anyPermissionDenied)
    {
        if (!outcome.Ok)
        {
            return Failed(code, outcome, evidence, now, findings, ref anyPermissionDenied);
        }

        var available = present(outcome.Data);
        return new EvProbeResult(new EvCapabilityCode(code), available ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable, evidence,
            available ? null : "Sonda não comprovou a capacidade.");
    }

    private static EvProbeResult CountCapability(
        string code, EvProbeOutcome outcome, string evidence, DateTimeOffset now, List<EvDiscoveryFinding> findings, ref bool anyPermissionDenied)
    {
        if (!outcome.Ok)
        {
            return Failed(code, outcome, evidence, now, findings, ref anyPermissionDenied);
        }

        var count = TryGetInt(outcome.Data, "count");
        if (count is null || count < 0)
        {
            findings.Add(new EvDiscoveryFinding(new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryOutputInvalid), new EvCapabilityCode(code), "contagem ausente/negativa", now, EvErrorCategory.OutputInvalid));
            return new EvProbeResult(new EvCapabilityCode(code), CapabilityAvailability.Indeterminate, evidence, "contagem inválida",
                new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryOutputInvalid), EvErrorCategory.OutputInvalid);
        }

        return new EvProbeResult(new EvCapabilityCode(code), count > 0 ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable, evidence,
            count > 0 ? null : "Nenhum item descoberto.");
    }

    private static EvProbeResult DerivedCapability(
        string code, EvProbeOutcome outcome, string evidence, DateTimeOffset now, List<EvDiscoveryFinding> findings, ref bool anyPermissionDenied)
    {
        if (!outcome.Ok)
        {
            return Failed(code, outcome, evidence, now, findings, ref anyPermissionDenied);
        }

        return new EvProbeResult(new EvCapabilityCode(code), CapabilityAvailability.Available, evidence, null);
    }

    private static EvProbeResult Failed(
        string code, EvProbeOutcome outcome, string evidence, DateTimeOffset now, List<EvDiscoveryFinding> findings, ref bool anyPermissionDenied)
    {
        if (outcome.Category == EvErrorCategory.PermissionDenied)
        {
            anyPermissionDenied = true;
        }

        var availability = outcome.Category == EvErrorCategory.PermissionDenied
            ? CapabilityAvailability.PermissionDenied
            : CapabilityAvailability.Indeterminate;
        findings.Add(new EvDiscoveryFinding(outcome.ErrorCode ?? new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryFailed), new EvCapabilityCode(code), outcome.Detail, now, outcome.Category));
        return new EvProbeResult(new EvCapabilityCode(code), availability, evidence, outcome.Detail, outcome.ErrorCode, outcome.Category);
    }

    private static EvExportSignature? TryReadSignature(JsonElement data, DateTimeOffset now)
    {
        if (!data.TryGetProperty("signature", out var sig) || sig.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var commandName = TryGetString(sig, "commandName");
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        return EvExportSignature.Create(
            commandName, TryGetString(sig, "moduleSource") ?? string.Empty, TryGetString(sig, "version") ?? string.Empty,
            TryGetString(sig, "commandType") ?? "Cmdlet", ReadArray(sig, "parameters"), ReadArray(sig, "mandatory"), ReadArray(sig, "parameterSets"), now);
    }

    private static string[] ReadArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.String).Select(static e => e.GetString()!).ToArray();
    }

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? TryGetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? TryGetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
}
