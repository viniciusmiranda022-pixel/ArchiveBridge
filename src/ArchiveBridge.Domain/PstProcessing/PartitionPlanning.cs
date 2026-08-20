using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Regra de planejamento de particionamento (Slice 4B, Passo 2). Função PURA e determinística: mesmas
/// entradas ⇒ mesmo plano (identidade, desfecho, motivo e partes), sem I/O, sem relógio próprio, sem
/// aleatoriedade e sem qualquer dependência de parser/fornecedor. Planejar NÃO toca o PST.
/// <para>
/// Ordem das decisões (todas fail-closed, a primeira que falhar decide):
/// </para>
/// <list type="number">
/// <item>sem inspeção canônica ⇒ <see cref="PartitionPlanReason.CanonicalInspectionUnavailable"/>;</item>
/// <item>inspeção não canônica ou hash incoerente com a custódia ⇒ <see cref="PartitionPlanReason.SourceHashDivergence"/>;</item>
/// <item>diagnóstico estrutural ≠ <see cref="PstStructuralDiagnostic.Valid"/> ⇒ <see cref="PartitionPlanReason.StructuralDiagnosticNotValid"/>;</item>
/// <item>sem tamanho observado ⇒ <see cref="PartitionPlanReason.ObservedSizeUnavailable"/>;</item>
/// <item>tamanho ≤ <see cref="PartitionPolicy.TargetPartBytes"/> ⇒ <see cref="PartitionPlanReason.SinglePartWithinTarget"/> (uma parte, o artefato inteiro);</item>
/// <item>caso contrário ⇒ <see cref="PartitionPlanReason.ItemInventoryUnavailable"/>.</item>
/// </list>
/// <para>
/// O último caso é o ponto honesto do Passo 2: dividir um PST acima do alvo exige agrupar por pasta/data
/// ou bin packing estável sobre um inventário de itens (runbook §20.1), e a inspeção canônica disponível
/// não produz contagem de itens/pastas nem offsets. Em vez de inventar boundaries, o plano registra a
/// incapacidade como estado explícito.
/// </para>
/// </summary>
public static class PartitionPlanning
{
    /// <summary>Calcula o plano determinístico para um artefato de custódia e sua inspeção canônica.</summary>
    /// <param name="custody">Artefato sob custódia (fonte da verdade de hash/escopo).</param>
    /// <param name="canonicalInspection">
    /// Inspeção canônica do artefato, ou <see langword="null"/> quando não existe nenhuma.
    /// </param>
    /// <param name="policy">Política de particionamento vigente.</param>
    /// <param name="planner">Nome/versão do planner.</param>
    /// <param name="correlation">Correlação de auditoria.</param>
    /// <param name="plannedAtUtc">Instante do planejamento, fornecido pelo chamador (determinismo).</param>
    public static PartitionPlan Plan(
        MigrationArtifact custody,
        PstInspectionRecord? canonicalInspection,
        PartitionPolicy policy,
        PartitionPlannerIdentity planner,
        CorrelationId correlation,
        DateTimeOffset plannedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(custody);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(planner);

        var (source, reason) = Evaluate(custody, canonicalInspection, policy);
        var planHash = PartitionPlanIdentity.ComputePlanHash(custody.Tenant, custody.Project, source, policy, planner);
        var parts = reason == PartitionPlanReason.SinglePartWithinTarget
            ? BuildSinglePart(planHash, source.SourceSizeBytes!.Value)
            : [];

        return PartitionPlan.Create(
            PartitionPlanId.New(), custody.Tenant, custody.Project, source, policy, planner, planHash,
            reason, parts, correlation, plannedAtUtc);
    }

    private static (PartitionPlanSource Source, PartitionPlanReason Reason) Evaluate(
        MigrationArtifact custody, PstInspectionRecord? canonicalInspection, PartitionPolicy policy)
    {
        // Sem inspeção canônica não há NADA observado sobre o artefato além do registro de custódia:
        // planejar aqui seria inventar. Fail-closed, sem identidade de inspeção no plano.
        if (canonicalInspection is null)
        {
            return (SourceWithoutInspection(custody), PartitionPlanReason.CanonicalInspectionUnavailable);
        }

        var source = new PartitionPlanSource(
            custody.Id,
            canonicalInspection.Id,
            custody.RegisteredHash,
            canonicalInspection.ObservedSizeBytes,
            canonicalInspection.EngineName,
            canonicalInspection.EngineVersion);

        // Defesa em profundidade: mesmo que a store devolva algo como "canônico", o Domain revalida a
        // canonicidade E a coerência com o hash REGISTRADO em custódia (staleness). Um artefato cujo hash
        // de custódia não é exatamente o hash da inspeção nunca origina plano.
        if (!canonicalInspection.IsCanonical
            || canonicalInspection.ExpectedHash != custody.RegisteredHash
            || canonicalInspection.ObservedHash != custody.RegisteredHash)
        {
            return (source, PartitionPlanReason.SourceHashDivergence);
        }

        if (canonicalInspection.Diagnostic != PstStructuralDiagnostic.Valid)
        {
            return (source, PartitionPlanReason.StructuralDiagnosticNotValid);
        }

        if (canonicalInspection.ObservedSizeBytes is not { } observedSize)
        {
            return (source, PartitionPlanReason.ObservedSizeUnavailable);
        }

        // Um artefato de tamanho zero nunca poderia ser um PST estruturalmente Valid; se ainda assim
        // chegasse aqui, não há origem real para planejar — fail-closed em vez de "plano de 0 byte".
        if (observedSize <= 0)
        {
            return (source, PartitionPlanReason.ObservedSizeUnavailable);
        }

        // Caso determinável sem inventário de itens: o artefato inteiro cabe em UMA parte dentro do alvo.
        // Nenhum split é necessário, portanto nenhum boundary de item/pasta precisa ser derivado. Acima do
        // alvo, o agrupamento por pasta/data/bin packing (runbook §20.1) exigiria inventário de itens que a
        // inspeção canônica não fornece — incapacidade explícita, nunca contagem/offset inventado.
        return observedSize <= policy.TargetPartBytes
            ? (source, PartitionPlanReason.SinglePartWithinTarget)
            : (source, PartitionPlanReason.ItemInventoryUnavailable);
    }

    private static PartitionPlanSource SourceWithoutInspection(MigrationArtifact custody) =>
        new(custody.Id, inspection: null, custody.RegisteredHash, sourceSizeBytes: null, engineName: null, engineVersion: null);

    private static PartitionPlanPart[] BuildSinglePart(Sha256Hash planHash, long sourceSizeBytes) =>
        [
            new PartitionPlanPart(
                PartitionPlanPartId.New(),
                sequence: 1,
                PartitionPlanIdentity.ComputePartKey(planHash, 1),
                sourceSizeBytes,
                coversEntireSource: true),
        ];
}
