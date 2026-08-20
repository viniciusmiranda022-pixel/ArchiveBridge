namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Motivo sanitizado e determinístico do desfecho do planejamento (persistido como <c>TINYINT</c>). É a
/// fonte ÚNICA do <see cref="PartitionPlanOutcome"/> (ver <see cref="PartitionPlanReasons.OutcomeOf"/>):
/// desfecho e motivo nunca podem divergir porque o desfecho é sempre derivado do motivo. Nenhum valor aqui
/// carrega caminho físico, conteúdo de mensagem ou qualquer dado de mailbox.
/// </summary>
public enum PartitionPlanReason
{
    /// <summary>
    /// O artefato inteiro cabe em UMA parte dentro do <see cref="PartitionPolicy.TargetPartBytes"/>: nenhum
    /// split é necessário e o plano não precisa de boundaries de item/pasta para ser executável.
    /// </summary>
    SinglePartWithinTarget = 0,

    /// <summary>
    /// O artefato excede o tamanho-alvo e exigiria split por pasta/data/bin packing (runbook §20.1), mas a
    /// inspeção canônica disponível não fornece inventário de itens/pastas — logo, não há como derivar
    /// boundaries reais. Modelado explicitamente como incapacidade, nunca substituído por valores inventados.
    /// </summary>
    ItemInventoryUnavailable = 1,

    /// <summary>Não existe inspeção canônica válida para o artefato + hash de custódia corrente.</summary>
    CanonicalInspectionUnavailable = 2,

    /// <summary>O hash da inspeção apresentada não corresponde ao hash registrado em custódia (fail-closed).</summary>
    SourceHashDivergence = 3,

    /// <summary>O diagnóstico estrutural da inspeção canônica não é <see cref="PstStructuralDiagnostic.Valid"/>.</summary>
    StructuralDiagnosticNotValid = 4,

    /// <summary>A inspeção canônica não traz tamanho observado — sem tamanho real não se planeja nada.</summary>
    ObservedSizeUnavailable = 5,
}

/// <summary>Mapeamento canônico motivo ⇒ desfecho (fonte única, usada por Domain, persistência e testes).</summary>
public static class PartitionPlanReasons
{
    /// <summary>Desfecho correspondente ao motivo. Todo motivo novo DEVE ser classificado aqui.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Motivo desconhecido (fail-closed).</exception>
    public static PartitionPlanOutcome OutcomeOf(PartitionPlanReason reason) => reason switch
    {
        PartitionPlanReason.SinglePartWithinTarget => PartitionPlanOutcome.Planned,
        PartitionPlanReason.ItemInventoryUnavailable => PartitionPlanOutcome.Unsupported,
        PartitionPlanReason.CanonicalInspectionUnavailable
            or PartitionPlanReason.SourceHashDivergence
            or PartitionPlanReason.StructuralDiagnosticNotValid
            or PartitionPlanReason.ObservedSizeUnavailable => PartitionPlanOutcome.Blocked,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Motivo de plano não classificado."),
    };
}
