using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>Identidade estável de um ambiente Enterprise Vault sob descoberta.</summary>
public readonly record struct EvEnvironmentId(Guid Value);

/// <summary>Identidade de um adapter de versão do Enterprise Vault (por capacidades, não por texto).</summary>
public readonly record struct EvAdapterId
{
    /// <summary>Cria um <see cref="EvAdapterId"/> não vazio.</summary>
    public EvAdapterId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("EvAdapterId não pode ser nulo ou vazio.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Identificador textual estável do adapter (ex.: <c>ev-capability-adapter-a</c>).</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identidade de uma execução de descoberta (uma sondagem única do ambiente).</summary>
public readonly record struct DiscoveryRunId(Guid Value)
{
    /// <summary>Gera um novo identificador de execução.</summary>
    public static DiscoveryRunId New() => new(Guid.NewGuid());
}

/// <summary>Versões de esquema estáveis da descoberta (parte do contexto durável e da evidência).</summary>
public static class EvDiscoverySchema
{
    /// <summary>Versão corrente do esquema de descoberta/evidência. Um esquema desconhecido é recusado.</summary>
    public const int Version = 1;

    /// <summary>Versão do envelope JSON de saída das sondas. Um schema desconhecido falha fechado.</summary>
    public const int ProbeEnvelopeVersion = 1;

    /// <summary>Versão do CATÁLOGO de sondas (scripts internos fixos). Faz parte do <c>ConfigurationHash</c>.</summary>
    public const int ProbeCatalogVersion = 1;

    /// <summary>Versão do CATÁLOGO de adapters registrados. Faz parte do <c>ConfigurationHash</c>.</summary>
    public const int AdapterCatalogVersion = 1;
}

/// <summary>
/// Identidade OBSERVADA de um ambiente Enterprise Vault (o que foi consultado e quando). A versão
/// observada é registrada como evidência, mas NÃO decide suporte — a decisão é por capacidades. Nomes
/// técnicos de servidor/site são informação sensível de infraestrutura (nunca vão para log).
/// </summary>
public sealed record EvEnvironmentIdentity(
    EvEnvironmentId EnvironmentId,
    string SiteName,
    string DirectoryServer,
    string ObservedVersion,
    string ProductVersion,
    string DiscoverySource,
    DateTimeOffset DiscoveredAtUtc);

/// <summary>
/// Uma capacidade DESCOBERTA do ambiente, com sua disponibilidade comprovada por evidência. Quando não
/// é <see cref="CapabilityAvailability.Available"/>, <see cref="BlockingReason"/> explica (sem PII) por
/// quê. <see cref="EvidenceReference"/> aponta a evidência (âncora/identificador de sonda), não valores.
/// </summary>
public sealed record EvCapability(
    EvCapabilityCode CapabilityCode,
    int CapabilityVersion,
    CapabilityAvailability Availability,
    string EvidenceReference,
    string? BlockingReason,
    DateTimeOffset DiscoveredAtUtc)
{
    /// <summary>Indica se a capacidade foi comprovada como disponível (evidência positiva).</summary>
    public bool IsAvailable => Availability == CapabilityAvailability.Available;
}

/// <summary>
/// Achado (finding) da descoberta que sustenta uma conclusão de bloqueio/insuficiência. Estruturado por
/// <see cref="EvDiscoveryResultCode"/> (não por mensagem), opcionalmente ligado a uma capacidade.
/// </summary>
public sealed record EvDiscoveryFinding(
    EvDiscoveryResultCode ResultCode,
    EvCapabilityCode? CapabilityCode,
    string Reason,
    DateTimeOffset DiscoveredAtUtc,
    EvErrorCategory ErrorCategory = EvErrorCategory.None);
