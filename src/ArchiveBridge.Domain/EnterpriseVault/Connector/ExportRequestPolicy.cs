using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.EnterpriseVault.Connector;

/// <summary>
/// Política de exportação VALIDADA (AB-4C-001 itens 9-11, critério 10) — NUNCA dispara exportação real
/// neste Passo (STOP-THE-LINE). Limites do cmdlet <c>Export-EVArchive</c> (§16.3 do runbook):
/// <c>-MaxPSTSizeMB</c> aceita o intervalo [<see cref="MinPstSizeMb"/>, <see cref="MaxPstSizeMbCeiling"/>];
/// o default do produto é <see cref="DefaultMaxPstSizeMb"/> (margem abaixo dos 20 GB recomendados pela
/// Microsoft — ADR-0013). <c>MaxThreads</c> aceita [<see cref="MinThreads"/>, <see cref="MaxThreadsCeiling"/>]
/// conforme benchmark documentado (§16.1).
/// </summary>
public sealed record ExportRequestPolicy
{
    /// <summary>Piso documentado do cmdlet para <c>-MaxPSTSizeMB</c>.</summary>
    public const int MinPstSizeMb = 500;

    /// <summary>Teto documentado do cmdlet para <c>-MaxPSTSizeMB</c>.</summary>
    public const int MaxPstSizeMbCeiling = 51200;

    /// <summary>Alvo operacional do produto (margem abaixo dos 20 GB do destino M365/Purview).</summary>
    public const int DefaultMaxPstSizeMb = 18432;

    /// <summary>Piso de threads simultâneas.</summary>
    public const int MinThreads = 1;

    /// <summary>Teto de threads simultâneas (envelope aprovado por benchmark).</summary>
    public const int MaxThreadsCeiling = 32;

    /// <summary>Default de threads simultâneas do runbook (§16.3).</summary>
    public const int DefaultMaxThreads = 8;

    private ExportRequestPolicy(int maxPstSizeMb, int maxThreads)
    {
        MaxPstSizeMb = maxPstSizeMb;
        MaxThreads = maxThreads;
    }

    /// <summary>Cria uma política validando ambos os limites — fail-closed fora do envelope documentado.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Um dos limites está fora do intervalo aceito.</exception>
    public static ExportRequestPolicy Create(int maxPstSizeMb, int maxThreads)
    {
        if (maxPstSizeMb < MinPstSizeMb || maxPstSizeMb > MaxPstSizeMbCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPstSizeMb), maxPstSizeMb, $"MaxPstSizeMb deve estar entre {MinPstSizeMb} e {MaxPstSizeMbCeiling}.");
        }

        if (maxThreads < MinThreads || maxThreads > MaxThreadsCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxThreads), maxThreads, $"MaxThreads deve estar entre {MinThreads} e {MaxThreadsCeiling}.");
        }

        return new ExportRequestPolicy(maxPstSizeMb, maxThreads);
    }

    /// <summary>Política default do produto (§16.3).</summary>
    public static ExportRequestPolicy Default { get; } = Create(DefaultMaxPstSizeMb, DefaultMaxThreads);

    /// <summary>Tamanho-alvo máximo por parte PST, em MB.</summary>
    public int MaxPstSizeMb { get; }

    /// <summary>Threads simultâneas máximas do exporter.</summary>
    public int MaxThreads { get; }
}

/// <summary>
/// Intenção de exportação VALIDADA mas NUNCA executada neste Passo (STOP-THE-LINE, AB-4C-001): captura o
/// connector, o archive-alvo (id externo opaco) e a política, para que Passos futuros implementem a
/// execução real sem reabrir a validação de limites. Não é um agregado persistido — apenas um value object
/// de intenção validada.
/// </summary>
public sealed record ExportRequest
{
    private const int ExternalArchiveIdMaxLength = 300;

    /// <summary>Cria uma intenção de exportação validada — sanitiza o id do archive na FORMA.</summary>
    public ExportRequest(ConnectorId connector, string externalArchiveId, ExportRequestPolicy policy, CorrelationId correlation)
    {
        Connector = connector;
        ExternalArchiveId = TextValue.Require(externalArchiveId, nameof(externalArchiveId), ExternalArchiveIdMaxLength);
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Correlation = correlation;
    }

    /// <summary>Connector responsável pela exportação (nunca executada nesta fatia).</summary>
    public ConnectorId Connector { get; }

    /// <summary>Identidade externa opaca do archive-alvo.</summary>
    public string ExternalArchiveId { get; }

    /// <summary>Política validada (limites de tamanho/threads).</summary>
    public ExportRequestPolicy Policy { get; }

    /// <summary>Correlação com a trilha de auditoria.</summary>
    public CorrelationId Correlation { get; }
}
