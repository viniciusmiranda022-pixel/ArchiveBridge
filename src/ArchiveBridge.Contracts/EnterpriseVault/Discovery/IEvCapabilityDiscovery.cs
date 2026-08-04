using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Descritor do ambiente a sondar (identidade lógica + alvos de directory/site). Sem credenciais: a
/// autenticação é do host de execução (privilégio mínimo), nunca passada aqui.
/// </summary>
public sealed record EvEnvironmentDescriptor(
    EvEnvironmentId EnvironmentId,
    string SiteName,
    string DirectoryServer);

/// <summary>
/// Porta de descoberta READ-ONLY de capacidades. Executa as sondas (via <see cref="IEvPowerShellExecutor"/>)
/// e devolve uma <see cref="EvDiscoveryObservation"/> FACTUAL — sem julgar suporte de versão e sem
/// modificar nada no Enterprise Vault. A interpretação por capacidades é dos adapters.
/// </summary>
public interface IEvCapabilityDiscovery
{
    /// <summary>Sonda o ambiente (read-only) sob a política e devolve as observações factuais.</summary>
    Task<EvDiscoveryObservation> ProbeAsync(
        EvEnvironmentDescriptor environment, EvDiscoveryPolicy policy, CancellationToken cancellationToken);
}

/// <summary>
/// Abstração de um adapter de VERSÃO por capacidades (ADR-0013). Reconhece um conjunto COMPROVADO de
/// capacidades, valida compatibilidade e declara requisitos — mas NÃO executa exportação neste Slice.
/// Não deve existir um adapter genérico que alegue suportar todas as versões do Enterprise Vault.
/// </summary>
public interface IEvVersionAdapter
{
    /// <summary>Identidade estável do adapter.</summary>
    EvAdapterId AdapterId { get; }

    /// <summary>Versão do adapter (parte da evidência e do desempate).</summary>
    int AdapterVersion { get; }

    /// <summary>
    /// Interpreta a observação: declara compatibilidade (Supported/Blocked/Unsupported) por capacidades,
    /// o conjunto de capacidades reconhecidas com sua evidência e os requisitos. Nunca decide por versão
    /// textual; nunca executa exportação.
    /// </summary>
    EvAdapterEvaluation Evaluate(EvDiscoveryObservation observation, EvDiscoveryPolicy policy);
}
