using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>
/// Interpreta uma observação factual + a seleção de adapter no <see cref="EvDiscoveryRunResult"/> final:
/// o conjunto de capacidades, o estado terminal (Ready/Blocked/Unsupported/Failed) e o código de
/// resultado estruturado. FAIL-CLOSED: uma capacidade exigida ausente/indeterminada bloqueia; ambiguidade
/// de adapter falha; achados fatais de sonda (conectividade/timeout/saída inválida) falham. Nunca declara
/// Ready por inferência de versão — só quando TODAS as capacidades exigidas foram comprovadas.
/// </summary>
public static class EvDiscoveryEvaluator
{
    /// <summary>Deriva o resultado avaliado da descoberta a partir da observação e da seleção de adapter.</summary>
    public static EvDiscoveryRunResult Evaluate(
        DiscoveryRunId runId,
        EvDiscoveryObservation observation,
        EvAdapterSelection selection,
        EvDiscoveryPolicy policy,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(policy);

        // 1) Achado fatal de sonda ⇒ Failed (antes de qualquer conclusão de capacidade).
        var fatal = FindFatal(observation.Findings);
        if (fatal is { } fatalFinding)
        {
            return Build(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                EvDiscoveryStatus.Failed, fatalFinding.ResultCode, CapabilitiesFromObservation(observation), adapterSelected: false, [fatalFinding]);
        }

        switch (selection.Outcome)
        {
            case AdapterSelectionOutcome.NotFound:
                return Terminal(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                    EvDiscoveryStatus.Unsupported, EvDiscoveryResultCodes.AdapterNotFound, CapabilitiesFromObservation(observation), adapterSelected: false);

            case AdapterSelectionOutcome.Ambiguous:
                return Terminal(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                    EvDiscoveryStatus.Failed, EvDiscoveryResultCodes.AdapterAmbiguous, CapabilitiesFromObservation(observation), adapterSelected: false);

            case AdapterSelectionOutcome.Unsupported:
                return Terminal(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                    EvDiscoveryStatus.Unsupported, EvDiscoveryResultCodes.VersionUnsupported, CapabilitiesFromObservation(observation), adapterSelected: false);

            case AdapterSelectionOutcome.Blocked:
                return Terminal(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                    EvDiscoveryStatus.Blocked, EvDiscoveryResultCodes.CapabilityIndeterminate, CapabilitiesFromObservation(observation), adapterSelected: false);

            case AdapterSelectionOutcome.Supported:
            default:
                var capabilities = selection.Selected!.Capabilities;
                var missing = FirstMissingRequired(policy, capabilities);
                if (missing is null)
                {
                    return Terminal(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                        EvDiscoveryStatus.Ready, EvDiscoveryResultCodes.DiscoveryCompleted, capabilities, adapterSelected: true);
                }

                return Terminal(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
                    EvDiscoveryStatus.Blocked, ResultCodeForMissing(missing.Value), capabilities, adapterSelected: true);
        }
    }

    private static EvDiscoveryRunResult Terminal(
        DiscoveryRunId runId, EvDiscoveryObservation observation, EvAdapterSelection selection, EvDiscoveryPolicy policy,
        DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc, EvDiscoveryStatus status, string resultCode,
        IReadOnlyList<EvCapability> capabilities, bool adapterSelected)
    {
        var findings = new List<EvDiscoveryFinding>(selection.Findings);
        if (status == EvDiscoveryStatus.Blocked)
        {
            foreach (var capability in policy.RequiredForReady)
            {
                var availability = AvailabilityOf(capabilities, capability.Value);
                if (availability != CapabilityAvailability.Available)
                {
                    findings.Add(new EvDiscoveryFinding(
                        new EvDiscoveryResultCode(ResultCodeForAvailability(availability, capability.Value)),
                        capability, $"Capacidade exigida não comprovada ({availability}).", completedAtUtc));
                }
            }
        }

        return Build(runId, observation, selection, policy, startedAtUtc, completedAtUtc,
            status, new EvDiscoveryResultCode(resultCode), capabilities, adapterSelected, findings);
    }

    private static EvDiscoveryRunResult Build(
        DiscoveryRunId runId, EvDiscoveryObservation observation, EvAdapterSelection selection, EvDiscoveryPolicy policy,
        DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc, EvDiscoveryStatus status, EvDiscoveryResultCode resultCode,
        IReadOnlyList<EvCapability> capabilities, bool adapterSelected, IReadOnlyList<EvDiscoveryFinding> findings)
    {
        var adapterId = adapterSelected ? selection.Selected!.AdapterId : (EvAdapterId?)null;
        var adapterVersion = adapterSelected ? selection.Selected!.AdapterVersion : (int?)null;
        var capabilitySet = EvCapabilitySet.Create(
            observation.Environment.EnvironmentId, adapterId, adapterVersion, EvDiscoverySchema.Version, capabilities, status);
        return new EvDiscoveryRunResult(
            runId, observation.Environment, capabilitySet, selection, observation.ExportSignature,
            findings, status, resultCode, startedAtUtc, completedAtUtc);
    }

    private static EvCapability[] CapabilitiesFromObservation(EvDiscoveryObservation observation) =>
        observation.ProbeResults
            .Select(static probe => new EvCapability(
                probe.CapabilityCode, 1, probe.Availability, probe.EvidenceReference, probe.BlockingReason, DateTimeOffset.UnixEpoch))
            .ToArray();

    private static EvCapabilityCode? FirstMissingRequired(EvDiscoveryPolicy policy, IReadOnlyList<EvCapability> capabilities)
    {
        foreach (var required in policy.RequiredForReady)
        {
            if (AvailabilityOf(capabilities, required.Value) != CapabilityAvailability.Available)
            {
                return required;
            }
        }

        return null;
    }

    private static CapabilityAvailability AvailabilityOf(IReadOnlyList<EvCapability> capabilities, string code)
    {
        foreach (var capability in capabilities)
        {
            if (string.Equals(capability.CapabilityCode.Value, code, StringComparison.Ordinal))
            {
                return capability.Availability;
            }
        }

        return CapabilityAvailability.Indeterminate;
    }

    private static EvDiscoveryFinding? FindFatal(IReadOnlyList<EvDiscoveryFinding> findings)
    {
        foreach (var finding in findings)
        {
            if (FatalCodes.Contains(finding.ResultCode.Value))
            {
                return finding;
            }
        }

        return null;
    }

    // Fatais = a MECÂNICA da sonda falhou (não se pode concluir nada). Falta de conectividade/permissão é
    // recuperável ⇒ Blocked, não Failed.
    private static readonly HashSet<string> FatalCodes = new(StringComparer.Ordinal)
    {
        EvDiscoveryResultCodes.DiscoveryTimeout,
        EvDiscoveryResultCodes.DiscoveryOutputInvalid,
    };

    private static string ResultCodeForMissing(EvCapabilityCode capability) => capability.Value switch
    {
        EvCapabilityCodes.EvRequiredPermissions => EvDiscoveryResultCodes.PermissionInsufficient,
        EvCapabilityCodes.EvExportCmdletAvailable => EvDiscoveryResultCodes.CmdletNotAvailable,
        EvCapabilityCodes.EvExportCmdletSignatureSupported => EvDiscoveryResultCodes.CmdletSignatureUnsupported,
        EvCapabilityCodes.EvVersionSupportedByAdapter => EvDiscoveryResultCodes.VersionUnsupported,
        EvCapabilityCodes.EvModuleAvailable => EvDiscoveryResultCodes.ModuleNotFound,
        EvCapabilityCodes.EvSnapinAvailable => EvDiscoveryResultCodes.SnapinNotFound,
        EvCapabilityCodes.EvDirectoryConnectivity => EvDiscoveryResultCodes.DirectoryUnreachable,
        _ => EvDiscoveryResultCodes.CapabilityIndeterminate,
    };

    private static string ResultCodeForAvailability(CapabilityAvailability availability, string capabilityCode) =>
        availability == CapabilityAvailability.PermissionDenied
            ? EvDiscoveryResultCodes.PermissionInsufficient
            : ResultCodeForMissing(new EvCapabilityCode(capabilityCode));
}
