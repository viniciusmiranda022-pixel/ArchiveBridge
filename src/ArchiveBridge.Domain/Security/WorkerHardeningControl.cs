namespace ArchiveBridge.Domain.Security;

/// <summary>
/// Controle de hardening da baseline de workers Windows on-premises (AB-I7-008 item 1, runbook
/// §30-37/§34). Cada valor corresponde a um controle citado explicitamente no escopo obrigatório do
/// work order — nenhum estado aspiracional. Persistido como <c>TINYINT</c> com o MESMO valor numérico
/// desta enum.
/// </summary>
public enum WorkerHardeningControl : byte
{
    /// <summary>SO suportado e com patching aplicado dentro da janela documentada.</summary>
    OsPatchingSupported = 0,

    /// <summary>Microsoft Defender for Endpoint com tamper protection habilitada no host.</summary>
    DefenderForEndpointTamperProtection = 1,

    /// <summary>App Control for Business/WDAC com allowlist ativa (ver <see cref="WdacPolicyEvidence"/>).</summary>
    AppControlWdacAllowlist = 2,

    /// <summary>Secure Boot, vTPM e Credential Guard, quando o hardware/hypervisor os suporta.</summary>
    SecureBootVTpmCredentialGuard = 3,

    /// <summary>BitLocker habilitado no(s) volume(s) do worker.</summary>
    BitLocker = 4,

    /// <summary>SMBv1 e protocolos TLS legados (&lt; TLS 1.2) desabilitados.</summary>
    SmbV1AndLegacyTlsDisabled = 5,

    /// <summary>RDP com deny-by-default (nenhuma exposição inbound aceita por padrão).</summary>
    RdpDenyByDefault = 6,

    /// <summary>Identidade de serviço do worker com privilégio mínimo (nunca conta administrativa genérica).</summary>
    ServiceIdentityLeastPrivilege = 7,

    /// <summary>ACL da área de scratch sem permissão de execução para dados processados.</summary>
    ScratchAclNoExecute = 8,

    /// <summary>Conectividade outbound restrita aos destinos documentados (allowlist de rede).</summary>
    OutboundRestricted = 9,

    /// <summary>Tratamento de crash dump (localização controlada, sem PII/segredo, retenção definida).</summary>
    CrashDumpHandling = 10,

    /// <summary>
    /// Aplicação de política de tenant do Microsoft Defender for Endpoint via Intune/Entra — controle
    /// Azure-only que a baseline on-premises aceita NÃO assume sem capability comprovada (item 1/
    /// STOP-THE-LINE: nenhuma dependência obrigatória de Azure PaaS). Ver
    /// <see cref="WorkerHardeningBaselineCatalog"/> — este é o único controle cuja
    /// <see cref="WorkerHardeningApplicability"/> é <see cref="WorkerHardeningApplicability.Unsupported"/>
    /// nesta baseline.
    /// </summary>
    MdeTenantPolicyEnforcement = 11,
}
