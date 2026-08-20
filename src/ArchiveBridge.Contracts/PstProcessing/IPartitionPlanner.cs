using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Porta do planner de particionamento (Slice 4B, Passo 2) — a fronteira que torna a estratégia de
/// planejamento SUBSTITUÍVEL sem tocar Domain/Application. Quando uma engine primária de leitura completa
/// de PST for aceita por ADR, um planner com inventário real de itens/pastas implementa esta mesma porta e
/// passa a produzir planos com múltiplas partes; nada em Domain/Application muda.
/// <para>
/// A assinatura é deliberadamente SÍNCRONA e sem <see cref="CancellationToken"/>: planejar é uma função
/// pura sobre dados já observados — não abre arquivo, não lê o PST, não executa split/rewrite/repair e não
/// tem efeito colateral algum. Nenhum tipo de fornecedor (Aspose, libpff, Outlook) pode atravessar esta
/// fronteira.
/// </para>
/// </summary>
public interface IPartitionPlanner
{
    /// <summary>Identidade estável do planner (participa da identidade determinística do plano).</summary>
    PartitionPlannerIdentity Identity { get; }

    /// <summary>Política de particionamento vigente (participa da identidade via fingerprint).</summary>
    PartitionPolicy Policy { get; }

    /// <summary>
    /// Calcula o plano determinístico para o artefato sob custódia. <paramref name="canonicalInspection"/>
    /// é <see langword="null"/> quando não existe inspeção canônica — nesse caso o plano resultante é
    /// sempre <see cref="PartitionPlanOutcome.Blocked"/>, nunca um plano executável.
    /// </summary>
    PartitionPlan Plan(
        MigrationArtifact custody,
        PstInspectionRecord? canonicalInspection,
        CorrelationId correlation,
        DateTimeOffset plannedAtUtc);
}
