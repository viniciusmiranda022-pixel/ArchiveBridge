namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Código de resultado ESTRUTURADO da descoberta (evita controle de fluxo por mensagem textual). Só
/// aceita um dos códigos conhecidos de <see cref="EvDiscoveryResultCodes"/> (fail-closed). Persistido
/// como o texto estável do código, permitindo correlação e decisão sem depender de mensagens.
/// </summary>
public readonly record struct EvDiscoveryResultCode
{
    /// <summary>Cria um código de resultado a partir de um valor conhecido; recusa desconhecidos.</summary>
    public EvDiscoveryResultCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !EvDiscoveryResultCodes.IsKnown(value))
        {
            throw new ArgumentException($"Código de resultado de descoberta EV desconhecido: '{value}'.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Texto estável do código (ex.: <c>EV_DISCOVERY_COMPLETED</c>).</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Registro fixo dos códigos de resultado da descoberta (fonte única de verdade).</summary>
public static class EvDiscoveryResultCodes
{
    /// <summary>Descoberta concluída com todas as sondas realizadas.</summary>
    public const string DiscoveryCompleted = "EV_DISCOVERY_COMPLETED";

    /// <summary>Descoberta parcial: algumas sondas ficaram indeterminadas (fail-closed nas ausentes).</summary>
    public const string DiscoveryPartial = "EV_DISCOVERY_PARTIAL";

    /// <summary>Descoberta falhou de forma geral.</summary>
    public const string DiscoveryFailed = "EV_DISCOVERY_FAILED";

    /// <summary>Falha de conectividade com o ambiente.</summary>
    public const string ConnectivityFailed = "EV_CONNECTIVITY_FAILED";

    /// <summary>Directory do Enterprise Vault inacessível.</summary>
    public const string DirectoryUnreachable = "EV_DIRECTORY_UNREACHABLE";

    /// <summary>Módulo do Enterprise Vault não encontrado.</summary>
    public const string ModuleNotFound = "EV_MODULE_NOT_FOUND";

    /// <summary>Snap-in do Enterprise Vault não encontrado.</summary>
    public const string SnapinNotFound = "EV_SNAPIN_NOT_FOUND";

    /// <summary>Cmdlet de exportação não disponível.</summary>
    public const string CmdletNotAvailable = "EV_CMDLET_NOT_AVAILABLE";

    /// <summary>Assinatura do cmdlet de exportação não suportada por nenhum adapter.</summary>
    public const string CmdletSignatureUnsupported = "EV_CMDLET_SIGNATURE_UNSUPPORTED";

    /// <summary>Permissão insuficiente para completar a descoberta.</summary>
    public const string PermissionInsufficient = "EV_PERMISSION_INSUFFICIENT";

    /// <summary>Versão do ambiente não suportada por nenhum adapter.</summary>
    public const string VersionUnsupported = "EV_VERSION_UNSUPPORTED";

    /// <summary>Uma ou mais capacidades ficaram indeterminadas.</summary>
    public const string CapabilityIndeterminate = "EV_CAPABILITY_INDETERMINATE";

    /// <summary>Nenhum adapter compatível encontrado.</summary>
    public const string AdapterNotFound = "EV_ADAPTER_NOT_FOUND";

    /// <summary>Mais de um adapter compatível sem desempate determinístico suficiente.</summary>
    public const string AdapterAmbiguous = "EV_ADAPTER_AMBIGUOUS";

    /// <summary>A descoberta excedeu o timeout.</summary>
    public const string DiscoveryTimeout = "EV_DISCOVERY_TIMEOUT";

    /// <summary>Saída da sonda inválida/malformada.</summary>
    public const string DiscoveryOutputInvalid = "EV_DISCOVERY_OUTPUT_INVALID";

    /// <summary>Evidência de descoberta inválida (hash/estrutura).</summary>
    public const string DiscoveryEvidenceInvalid = "EV_DISCOVERY_EVIDENCE_INVALID";

    /// <summary>Contexto do comando durável obsoleto.</summary>
    public const string StaleCommandContext = "STALE_COMMAND_CONTEXT";

    /// <summary>Cercamento perdido (época/dono/lease inválido).</summary>
    public const string FencedOut = "FENCED_OUT";

    private static readonly string[] AllCodes =
    [
        DiscoveryCompleted, DiscoveryPartial, DiscoveryFailed, ConnectivityFailed, DirectoryUnreachable,
        ModuleNotFound, SnapinNotFound, CmdletNotAvailable, CmdletSignatureUnsupported, PermissionInsufficient,
        VersionUnsupported, CapabilityIndeterminate, AdapterNotFound, AdapterAmbiguous, DiscoveryTimeout,
        DiscoveryOutputInvalid, DiscoveryEvidenceInvalid, StaleCommandContext, FencedOut,
    ];

    private static readonly HashSet<string> KnownSet = new(AllCodes, StringComparer.Ordinal);

    /// <summary>Todos os códigos de resultado conhecidos, em ordem estável.</summary>
    public static IReadOnlyList<string> All => AllCodes;

    /// <summary>Indica se <paramref name="value"/> é um código de resultado conhecido.</summary>
    public static bool IsKnown(string value) => KnownSet.Contains(value);
}
