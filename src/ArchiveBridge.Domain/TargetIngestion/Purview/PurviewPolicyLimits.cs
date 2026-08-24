using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>
/// Limites do policy/capacity gate do adapter Purview (runbook §25.4, work order AB-I5-001 itens 6-10).
/// Reutiliza, sem duplicar, os limites já documentados/testados em outros módulos —
/// <see cref="CapacityRule.OneHundredGigabytesInBytes"/> (o limite principal por archive, §25.4/§27),
/// <see cref="PartitionPolicy.RunbookHardPartBytes"/> (limite duro por parte, §20.1) e
/// <see cref="MappingSchema.MaxDataRows"/> (limite de linhas do CSV, §25.8) — mudar qualquer um desses
/// muda este gate automaticamente, sem risco de os números divergirem entre módulos.
/// <see cref="SafetyMarginBytes"/> é a ÚNICA constante NÃO documentada pela Microsoft: é política própria e
/// configurável do produto.
/// </summary>
public sealed record PurviewPolicyLimits
{
    /// <summary>
    /// Margem de segurança default: política PRÓPRIA do produto (não documentada pela Microsoft) — 1 GiB
    /// conservador, subtraído da capacidade disponível observada antes de comparar com o volume planejado.
    /// </summary>
    public const long DefaultSafetyMarginBytes = 1L * 1024 * 1024 * 1024;

    private PurviewPolicyLimits(long mainArchiveImportLimitBytes, long hardPartBytes, int maxCsvDataRows, long safetyMarginBytes)
    {
        MainArchiveImportLimitBytes = mainArchiveImportLimitBytes;
        HardPartBytes = hardPartBytes;
        MaxCsvDataRows = maxCsvDataRows;
        SafetyMarginBytes = safetyMarginBytes;
    }

    /// <summary>Cria uma política explícita. Fail-closed: limites não positivos são recusados, nunca "corrigidos" silenciosamente.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Algum limite não é positivo.</exception>
    public static PurviewPolicyLimits Create(long mainArchiveImportLimitBytes, long hardPartBytes, int maxCsvDataRows, long safetyMarginBytes)
    {
        if (mainArchiveImportLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mainArchiveImportLimitBytes), mainArchiveImportLimitBytes, "MainArchiveImportLimitBytes deve ser > 0.");
        }

        if (hardPartBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardPartBytes), hardPartBytes, "HardPartBytes deve ser > 0.");
        }

        if (maxCsvDataRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCsvDataRows), maxCsvDataRows, "MaxCsvDataRows deve ser > 0.");
        }

        if (safetyMarginBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safetyMarginBytes), safetyMarginBytes, "SafetyMarginBytes não pode ser negativo.");
        }

        return new PurviewPolicyLimits(mainArchiveImportLimitBytes, hardPartBytes, maxCsvDataRows, safetyMarginBytes);
    }

    /// <summary>Política default: limites documentados (runbook §25.4/§20.1/§25.8) + margem de segurança própria do produto.</summary>
    public static PurviewPolicyLimits RunbookDefault { get; } = Create(
        CapacityRule.OneHundredGigabytesInBytes,
        PartitionPolicy.RunbookHardPartBytes,
        MappingSchema.MaxDataRows,
        DefaultSafetyMarginBytes);

    /// <summary>Limite principal de import por archive (runbook §25.4/§27) — <c>provider.mainArchiveImportLimitBytes</c>.</summary>
    public long MainArchiveImportLimitBytes { get; }

    /// <summary>Limite duro por parte planejada (runbook §20.1).</summary>
    public long HardPartBytes { get; }

    /// <summary>Máximo de linhas de dados do CSV mapping (runbook §25.8).</summary>
    public int MaxCsvDataRows { get; }

    /// <summary>Margem de segurança subtraída da capacidade disponível observada (política própria do produto).</summary>
    public long SafetyMarginBytes { get; }
}
