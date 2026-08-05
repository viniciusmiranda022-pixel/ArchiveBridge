namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Código TIPADO de uma capacidade do Enterprise Vault (evita strings soltas). Só aceita um dos códigos
/// conhecidos de <see cref="EvCapabilityCodes"/>; um código desconhecido é recusado na construção
/// (fail-closed). Persistido como o texto estável do código.
/// </summary>
public readonly record struct EvCapabilityCode
{
    /// <summary>Cria um código de capacidade a partir de um valor conhecido; recusa desconhecidos.</summary>
    public EvCapabilityCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !EvCapabilityCodes.IsKnown(value))
        {
            throw new ArgumentException($"Código de capacidade EV desconhecido: '{value}'.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Texto estável do código (ex.: <c>EV_EXPORT_CMDLET_AVAILABLE</c>).</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Registro fixo das capacidades mínimas avaliadas na descoberta. Fonte única de verdade do conjunto
/// conhecido — qualquer código fora desta lista é recusado. Uma capacidade só é declarada
/// <see cref="CapabilityAvailability.Available"/> com evidência positiva; nunca por inferência da versão.
/// </summary>
public static class EvCapabilityCodes
{
    /// <summary>PowerShell disponível no host de execução autorizado.</summary>
    public const string EvPowershellAvailable = "EV_POWERSHELL_AVAILABLE";

    /// <summary>Módulo do Enterprise Vault carregável.</summary>
    public const string EvModuleAvailable = "EV_MODULE_AVAILABLE";

    /// <summary>Snap-in do Enterprise Vault registrado/carregável.</summary>
    public const string EvSnapinAvailable = "EV_SNAPIN_AVAILABLE";

    /// <summary>Conectividade com o Directory do Enterprise Vault.</summary>
    public const string EvDirectoryConnectivity = "EV_DIRECTORY_CONNECTIVITY";

    /// <summary>Descoberta de sites do Enterprise Vault.</summary>
    public const string EvSiteDiscovery = "EV_SITE_DISCOVERY";

    /// <summary>Descoberta de servidores do Enterprise Vault.</summary>
    public const string EvServerDiscovery = "EV_SERVER_DISCOVERY";

    /// <summary>Descoberta de vault stores.</summary>
    public const string EvVaultStoreDiscovery = "EV_VAULT_STORE_DISCOVERY";

    /// <summary>Descoberta de archives.</summary>
    public const string EvArchiveDiscovery = "EV_ARCHIVE_DISCOVERY";

    /// <summary>O cmdlet de exportação (<c>Export-EVArchive</c>) está presente no ambiente.</summary>
    public const string EvExportCmdletAvailable = "EV_EXPORT_CMDLET_AVAILABLE";

    /// <summary>A assinatura observada do cmdlet de exportação é suportada por um adapter.</summary>
    public const string EvExportCmdletSignatureSupported = "EV_EXPORT_CMDLET_SIGNATURE_SUPPORTED";

    /// <summary>A exportação para PST é suportada pela assinatura observada.</summary>
    public const string EvExportPstSupported = "EV_EXPORT_PST_SUPPORTED";

    /// <summary>O parâmetro de tamanho/limite de PST é suportado.</summary>
    public const string EvExportSizeParameterSupported = "EV_EXPORT_SIZE_PARAMETER_SUPPORTED";

    /// <summary>A geração de relatório de exportação é suportada.</summary>
    public const string EvExportReportSupported = "EV_EXPORT_REPORT_SUPPORTED";

    /// <summary>A filtragem na exportação é suportada.</summary>
    public const string EvExportFilteringSupported = "EV_EXPORT_FILTERING_SUPPORTED";

    /// <summary>Acesso ao caminho de staging (leitura/escrita comprovada na descoberta, read-only).</summary>
    public const string EvStagingPathAccess = "EV_STAGING_PATH_ACCESS";

    /// <summary>As permissões mínimas exigidas estão presentes.</summary>
    public const string EvRequiredPermissions = "EV_REQUIRED_PERMISSIONS";

    /// <summary>A versão observada é suportada por um adapter (por capacidade, não por texto de versão).</summary>
    public const string EvVersionSupportedByAdapter = "EV_VERSION_SUPPORTED_BY_ADAPTER";

    private static readonly string[] AllCodes =
    [
        EvPowershellAvailable, EvModuleAvailable, EvSnapinAvailable, EvDirectoryConnectivity,
        EvSiteDiscovery, EvServerDiscovery, EvVaultStoreDiscovery, EvArchiveDiscovery,
        EvExportCmdletAvailable, EvExportCmdletSignatureSupported, EvExportPstSupported,
        EvExportSizeParameterSupported, EvExportReportSupported, EvExportFilteringSupported,
        EvStagingPathAccess, EvRequiredPermissions, EvVersionSupportedByAdapter,
    ];

    private static readonly HashSet<string> KnownSet = new(AllCodes, StringComparer.Ordinal);

    /// <summary>Todos os códigos de capacidade conhecidos, em ordem estável.</summary>
    public static IReadOnlyList<string> All => AllCodes;

    /// <summary>Todos os códigos como value objects tipados, em ordem estável.</summary>
    public static IReadOnlyList<EvCapabilityCode> AllCapabilities =>
        Array.ConvertAll(AllCodes, static code => new EvCapabilityCode(code));

    /// <summary>Indica se <paramref name="value"/> é um código de capacidade conhecido.</summary>
    public static bool IsKnown(string value) => KnownSet.Contains(value);
}
