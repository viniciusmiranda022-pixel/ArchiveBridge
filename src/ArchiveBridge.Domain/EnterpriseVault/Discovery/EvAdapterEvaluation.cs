namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>Compatibilidade DECLARADA por um adapter para uma observação (por capacidades, não por versão).</summary>
public enum AdapterCompatibility
{
    /// <summary>O adapter reconhece o conjunto observado e pode declarar as capacidades exigidas.</summary>
    Supported,

    /// <summary>O adapter reconhece o ambiente, mas falta capacidade/permissão comprovada (recuperável).</summary>
    Blocked,

    /// <summary>O adapter não reconhece o ambiente/assinatura observada (não é o adapter certo).</summary>
    Unsupported,
}

/// <summary>Um requisito declarado por um adapter (capacidade que ele exige para operar).</summary>
public sealed record EvAdapterRequirement(EvCapabilityCode CapabilityCode, string Description);

/// <summary>
/// Interpretação de um adapter sobre uma observação: a compatibilidade declarada, o conjunto de
/// capacidades que ele reconhece a partir da evidência, seus requisitos e achados. A
/// <see cref="Precedence"/> é o critério determinístico de desempate entre adapters compatíveis (maior
/// vence); um empate de precedência entre compatíveis é AMBÍGUO e falha fechado.
/// </summary>
public sealed record EvAdapterEvaluation(
    EvAdapterId AdapterId,
    int AdapterVersion,
    AdapterCompatibility Compatibility,
    IReadOnlyList<EvCapability> Capabilities,
    IReadOnlyList<EvAdapterRequirement> Requirements,
    IReadOnlyList<EvDiscoveryFinding> Findings,
    int Precedence,
    string? ProfileId = null,
    EvExportMaturity? Maturity = null);
