using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Workers.Pst;

/// <summary>
/// Configuração tipada e FAIL-CLOSED da capacidade de PLANEJAMENTO de particionamento (Slice 4B, Passo 2).
/// O padrão é <c>Enabled=false</c>: sem habilitação explícita, nenhum serviço de planejamento é registrado.
/// <para>
/// Os limites default NÃO são escolhidos aqui: vêm da política documentada no runbook §20.1, expostos por
/// <see cref="PartitionPolicy.RunbookDefault"/> (fonte única). Sobrescrevê-los é uma decisão de implantação
/// explícita e AUDITÁVEL — muda o fingerprint da política, muda a identidade de todo plano futuro e nunca
/// reaproveita um plano calculado sob a política anterior. Valores inválidos derrubam o host no startup
/// (nunca são "corrigidos" para um valor arbitrário).
/// </para>
/// </summary>
public sealed class PstPartitionPlanningOptions
{
    /// <summary>Seção de configuração da capacidade de planejamento de particionamento.</summary>
    public const string SectionName = "PstPartitionPlanning";

    /// <summary>Habilita o registro dos serviços de planejamento. Padrão <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Tamanho-alvo por parte, em bytes. Default: runbook §20.1 (18 GiB).</summary>
    public long TargetPartBytes { get; set; } = PartitionPolicy.RunbookTargetPartBytes;

    /// <summary>Limite duro por parte, em bytes. Default: runbook §20.1 (20 GB).</summary>
    public long HardPartBytes { get; set; } = PartitionPolicy.RunbookHardPartBytes;

    /// <summary>
    /// Valida a configuração OPERACIONAL e materializa a política. Fail-closed: planejamento exige a
    /// capacidade de inspeção habilitada (um plano só nasce de inspeção canônica) e limites coerentes.
    /// </summary>
    /// <exception cref="PstInspectionConfigurationException">Configuração inválida — o host não deve subir.</exception>
    public PartitionPolicy ValidateForOperation(bool inspectionEnabled)
    {
        var errors = new List<string>();
        if (!inspectionEnabled)
        {
            errors.Add($"{SectionName}:Enabled exige {PstInspectionOptions.SectionName}:Enabled=true (um plano só nasce de inspeção canônica).");
        }

        if (TargetPartBytes <= 0)
        {
            errors.Add($"{SectionName}:TargetPartBytes deve ser > 0.");
        }

        if (HardPartBytes <= 0)
        {
            errors.Add($"{SectionName}:HardPartBytes deve ser > 0.");
        }

        if (TargetPartBytes > 0 && HardPartBytes > 0 && TargetPartBytes > HardPartBytes)
        {
            errors.Add($"{SectionName}:TargetPartBytes não pode exceder {SectionName}:HardPartBytes.");
        }

        if (errors.Count > 0)
        {
            throw new PstInspectionConfigurationException(
                "Configuração inválida da capacidade de planejamento de particionamento (fail-closed): " + string.Join(" ", errors));
        }

        return PartitionPolicy.Create(TargetPartBytes, HardPartBytes);
    }
}
