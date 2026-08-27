namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Catálogo FIXO e versionado da baseline de hardening dos workers Windows (AB-I7-008 item 1) — a ÚNICA
/// fonte de <see cref="WorkerHardeningApplicability"/> por <see cref="WorkerHardeningControl"/>. Nenhum
/// chamador informa a aplicabilidade diretamente: ela é sempre derivada daqui, para que nenhuma
/// identidade/papel possa reclassificar um controle (defesa contra "privilege spoofing" de
/// aplicabilidade).
/// </summary>
public static class WorkerHardeningBaselineCatalog
{
    /// <summary>Versão do catálogo — gravada em toda evidência nova, nunca reescrita.</summary>
    public const string CurrentBaselineVersion = "archivebridge.security.worker-hardening-baseline.v1";

    private static readonly Dictionary<WorkerHardeningControl, WorkerHardeningApplicability> Applicabilities =
        new Dictionary<WorkerHardeningControl, WorkerHardeningApplicability>
        {
            [WorkerHardeningControl.OsPatchingSupported] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.DefenderForEndpointTamperProtection] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.AppControlWdacAllowlist] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.SecureBootVTpmCredentialGuard] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.BitLocker] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.SmbV1AndLegacyTlsDisabled] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.RdpDenyByDefault] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.ServiceIdentityLeastPrivilege] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.ScratchAclNoExecute] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.OutboundRestricted] = WorkerHardeningApplicability.Required,
            [WorkerHardeningControl.CrashDumpHandling] = WorkerHardeningApplicability.Required,

            // Azure-only (Intune/Entra tenant policy) — a baseline on-premises aceita não introduz
            // dependência obrigatória de Azure PaaS (STOP-THE-LINE do work order); permanece Unsupported
            // até uma capability real e comprovada existir para o host on-premises.
            [WorkerHardeningControl.MdeTenantPolicyEnforcement] = WorkerHardeningApplicability.Unsupported,
        };

    /// <summary>Todos os controles cobertos por esta baseline, na ordem declarada da enum.</summary>
    public static IReadOnlyList<WorkerHardeningControl> AllControls { get; } =
        Enum.GetValues<WorkerHardeningControl>();

    /// <summary>Aplicabilidade FIXA de um controle nesta baseline — nunca sobrescrevível pelo chamador.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="control"/> não pertence a esta baseline.</exception>
    public static WorkerHardeningApplicability Applicability(WorkerHardeningControl control)
    {
        if (!Applicabilities.TryGetValue(control, out var applicability))
        {
            throw new ArgumentOutOfRangeException(nameof(control), control, "Controle de hardening desconhecido na baseline aceita.");
        }

        return applicability;
    }
}
