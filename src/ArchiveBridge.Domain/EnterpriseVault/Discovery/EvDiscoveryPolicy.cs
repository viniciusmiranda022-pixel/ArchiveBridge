namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Política versionada de descoberta: quais capacidades precisam ser comprovadas para um ambiente ser
/// <see cref="EvDiscoveryStatus.Ready"/>, além de limites de sonda (timeout e tamanho máximo de saída).
/// A política é parte do contexto durável (o worker recusa um comando cuja política divergiu) e da
/// configuração que compõe o <c>ConfigurationHash</c>. Não decide suporte por versão textual.
/// </summary>
public sealed record EvDiscoveryPolicy(
    int PolicyVersion,
    IReadOnlyList<EvCapabilityCode> RequiredForReady,
    long MaxOutputBytes,
    TimeSpan ProbeTimeout)
{
    /// <summary>Versão corrente da política. Uma política desconhecida é recusada.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Política padrão: exige as capacidades essenciais para declarar o ambiente pronto para exportação
    /// (PowerShell, conectividade de directory, cmdlet de exportação presente com assinatura/PST
    /// suportados, permissões mínimas e versão suportada por adapter). Limite de 1 MiB de saída e 5 min.
    /// </summary>
    public static EvDiscoveryPolicy Default => new(
        CurrentVersion,
        [
            new EvCapabilityCode(EvCapabilityCodes.EvPowershellAvailable),
            new EvCapabilityCode(EvCapabilityCodes.EvDirectoryConnectivity),
            new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletAvailable),
            new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletSignatureSupported),
            new EvCapabilityCode(EvCapabilityCodes.EvExportPstSupported),
            new EvCapabilityCode(EvCapabilityCodes.EvRequiredPermissions),
            new EvCapabilityCode(EvCapabilityCodes.EvVersionSupportedByAdapter),
        ],
        MaxOutputBytes: 1L * 1024 * 1024,
        ProbeTimeout: TimeSpan.FromMinutes(5));
}
