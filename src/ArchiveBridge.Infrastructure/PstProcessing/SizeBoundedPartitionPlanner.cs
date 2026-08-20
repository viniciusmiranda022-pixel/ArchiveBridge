using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Adapter de planejamento do Slice 4B, Passo 2 — a única implementação de <see cref="IPartitionPlanner"/>
/// deste Passo. Delega integralmente a decisão à regra PURA do Domain (<see cref="PartitionPlanning"/>) e
/// não possui I/O, estado mutável, relógio próprio ou dependência de biblioteca de fornecedor: planejar é
/// read-only e determinístico.
/// <para>
/// Ele planeja apenas o que é derivável da inspeção canônica do Passo 1 (hash, tamanho, diagnóstico
/// estrutural): um artefato dentro do <see cref="PartitionPolicy.TargetPartBytes"/> vira UMA parte que
/// cobre a origem inteira; acima disso, o split exigiria inventário de itens/pastas que nenhuma engine
/// aceita por ADR fornece hoje — e o plano registra essa incapacidade explicitamente
/// (<see cref="PartitionPlanOutcome.Unsupported"/>) em vez de inventar boundaries. Um planner com
/// inventário real implementa esta MESMA porta no futuro, sem tocar Domain/Application.
/// </para>
/// </summary>
public sealed class SizeBoundedPartitionPlanner : IPartitionPlanner
{
    /// <summary>Nome estável deste adapter (evidência/auditoria e identidade do plano).</summary>
    public const string Name = "SizeBoundedPartitionPlanner";

    /// <summary>Versão deste adapter. Mudá-la muda a identidade determinística de todo plano futuro.</summary>
    public const string Version = "1.0";

    /// <summary>Cria o planner para a política informada (fail-closed: política é obrigatória).</summary>
    public SizeBoundedPartitionPlanner(PartitionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
    }

    /// <inheritdoc />
    public PartitionPlannerIdentity Identity { get; } = new(Name, Version);

    /// <inheritdoc />
    public PartitionPolicy Policy { get; }

    /// <inheritdoc />
    public PartitionPlan Plan(
        MigrationArtifact custody,
        PstInspectionRecord? canonicalInspection,
        CorrelationId correlation,
        DateTimeOffset plannedAtUtc) =>
        PartitionPlanning.Plan(custody, canonicalInspection, Policy, Identity, correlation, plannedAtUtc);
}
