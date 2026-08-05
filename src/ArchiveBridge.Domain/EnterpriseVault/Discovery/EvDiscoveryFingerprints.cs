using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>
/// Configuração COMPLETA que enquadra uma descoberta e cujo hash identifica a reserva. Inclui o ambiente,
/// a versão/hash de configuração do projeto esperados, a versão da política, a lista ORDENADA de
/// capacidades exigidas, os limites (bytes/timeout) e as versões de esquema, catálogo de sondas e catálogo
/// de adapters — de modo que uma mudança em qualquer um desses fatores produza um <see cref="ComputeHash"/>
/// diferente e NÃO reutilize uma reserva incompatível.
/// </summary>
public sealed record EvDiscoveryConfiguration(
    EvEnvironmentId EnvironmentId,
    int ExpectedProjectConfigurationVersion,
    Sha256Hash ExpectedConfigurationHash,
    int PolicyVersion,
    IReadOnlyList<EvCapabilityCode> RequiredForReady,
    long MaxOutputBytes,
    TimeSpan ProbeTimeout,
    int DiscoverySchemaVersion,
    int ProbeCatalogVersion,
    int AdapterCatalogVersion)
{
    /// <summary>Constrói a configuração a partir da política e do contexto de projeto esperado.</summary>
    public static EvDiscoveryConfiguration For(
        EvEnvironmentId environmentId, EvDiscoveryPolicy policy, int expectedProjectConfigurationVersion, Sha256Hash expectedConfigurationHash)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new EvDiscoveryConfiguration(
            environmentId,
            expectedProjectConfigurationVersion,
            expectedConfigurationHash,
            policy.PolicyVersion,
            [.. policy.RequiredForReady.OrderBy(static code => code.Value, StringComparer.Ordinal)],
            policy.MaxOutputBytes,
            policy.ProbeTimeout,
            EvDiscoverySchema.Version,
            EvDiscoverySchema.ProbeCatalogVersion,
            EvDiscoverySchema.AdapterCatalogVersion);
    }

    /// <summary>Hash determinístico da configuração completa.</summary>
    public Sha256Hash ComputeHash() => DeterministicHash.Compute(
    [
        "env", EnvironmentId.Value.ToString("N"),
        "projCfgVer", ExpectedProjectConfigurationVersion.ToString(CultureInfo.InvariantCulture),
        "projCfgHash", ExpectedConfigurationHash.Value,
        "policyVer", PolicyVersion.ToString(CultureInfo.InvariantCulture),
        "required", string.Join(",", RequiredForReady.Select(static code => code.Value)),
        "maxBytes", MaxOutputBytes.ToString(CultureInfo.InvariantCulture),
        "timeoutTicks", ProbeTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
        "schema", DiscoverySchemaVersion.ToString(CultureInfo.InvariantCulture),
        "probeCatalog", ProbeCatalogVersion.ToString(CultureInfo.InvariantCulture),
        "adapterCatalog", AdapterCatalogVersion.ToString(CultureInfo.InvariantCulture),
    ]);
}

/// <summary>
/// Impressão digital SEMÂNTICA COMPLETA de uma execução de descoberta. É o SHA-256 calculado DIRETAMENTE
/// sobre a codificação canônica TIPADA e length-prefixed de <see cref="EvDiscoveryCanonical"/> — sem
/// separadores nem sentinelas, com marcador de presença em todos os anuláveis (<c>null</c> ≠ <c>""</c> ≠
/// <c>0</c> ≠ <c>"&lt;none&gt;"</c>). Cobre identidade integral do ambiente, capacidades, assinatura, a
/// seleção completa e cada avaliação de adapter por inteiro (maturidade, requisitos, capacidades declaradas
/// e achados), com <see cref="EvErrorCategory"/> em cada achado. As invariantes de unicidade são validadas
/// (fail-closed) antes do cálculo. Qualquer diferença semântica muda o hash.
/// </summary>
public static class EvDiscoverySemanticFingerprint
{
    /// <summary>Computa o hash semântico completo sobre os bytes canônicos (fail-closed nas invariantes).</summary>
    public static Sha256Hash Compute(EvDiscoveryRunResult result) =>
        DeterministicHash.ComputeBytes(EvDiscoveryCanonical.Encode(result));
}
