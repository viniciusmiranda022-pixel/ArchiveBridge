namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Desfecho de UMA tentativa de planejamento de particionamento (persistido como <c>TINYINT</c>, mesmo
/// valor numérico no banco). Derivado sempre de <see cref="PartitionPlanReason"/> (ver
/// <see cref="PartitionPlanReasons.OutcomeOf"/>) — nunca informado separadamente, para que desfecho e
/// motivo não possam divergir. Só <see cref="Planned"/> produz partes e pode se tornar canônico;
/// <see cref="Unsupported"/> e <see cref="Blocked"/> são evidência append-only, jamais reaproveitados.
/// </summary>
public enum PartitionPlanOutcome
{
    /// <summary>Plano determinístico calculado, com partes explícitas derivadas de informação real.</summary>
    Planned = 0,

    /// <summary>
    /// Pré-condições válidas, mas o inventário disponível NÃO permite derivar boundaries executáveis.
    /// Estado explícito e honesto — nunca contagens/offsets/partições inventados.
    /// </summary>
    Unsupported = 1,

    /// <summary>Pré-condição de custódia/inspeção não satisfeita: planejar seria inseguro (fail-closed).</summary>
    Blocked = 2,
}
