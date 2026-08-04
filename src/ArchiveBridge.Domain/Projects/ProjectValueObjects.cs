using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Projects;

/// <summary>Nome do projeto de migração (obrigatório, sem caracteres de controle).</summary>
public readonly record struct ProjectName
{
    private const int MaxLength = 200;

    /// <summary>Cria um nome de projeto válido.</summary>
    public ProjectName(string value) => Value = TextValue.Require(value, nameof(value), MaxLength);

    /// <summary>Texto do nome.</summary>
    public string Value { get; }
}

/// <summary>Responsável pelo projeto (obrigatório). Identificador de principal, não PII de conteúdo.</summary>
public readonly record struct ProjectOwner
{
    private const int MaxLength = 200;

    /// <summary>Cria um owner válido.</summary>
    public ProjectOwner(string value) => Value = TextValue.Require(value, nameof(value), MaxLength);

    /// <summary>Texto do owner.</summary>
    public string Value { get; }
}

/// <summary>
/// Tenant de destino (Microsoft 365) — parte obrigatória e inequívoca do destino. Não é segredo,
/// SAS nem token; é o identificador do tenant (ex.: domínio onmicrosoft).
/// </summary>
public readonly record struct TargetTenant
{
    private const int MaxLength = 256;

    /// <summary>Cria um tenant de destino válido (obrigatório).</summary>
    public TargetTenant(string value)
    {
        var normalized = TextValue.Require(value, nameof(value), MaxLength);
        if (normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException("TargetTenant não pode conter espaços.", nameof(value));
        }

        Value = normalized;
    }

    /// <summary>Identificador do tenant de destino.</summary>
    public string Value { get; }
}

/// <summary>
/// Política de arquivamento no destino. Enum concreto — o destino nunca é ambíguo. A habilitação
/// real do Purview é de slice futuro; aqui apenas o modelo de configuração.
/// </summary>
public enum TargetArchivePolicy
{
    /// <summary>Importar como Online Archive (arquivo do usuário).</summary>
    OnlineArchive,

    /// <summary>Importar para a caixa primária.</summary>
    PrimaryMailbox,
}

/// <summary>Versão monotônica da configuração de projeto (começa em 1).</summary>
public readonly record struct ConfigurationVersion(int Value)
{
    /// <summary>Primeira versão de configuração.</summary>
    public static ConfigurationVersion Initial => new(1);

    /// <summary>Próxima versão (uma alteração de configuração sempre cria uma nova versão).</summary>
    public ConfigurationVersion Next() => new(Value + 1);
}
