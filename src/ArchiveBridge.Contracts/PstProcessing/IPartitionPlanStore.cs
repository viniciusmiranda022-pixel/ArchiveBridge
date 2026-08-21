using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Persistência append-only dos planos de particionamento (checkpoint do Passo 2). Tenant/projeto scoped
/// (RLS + filtro explícito por projeto). <see cref="FindCanonicalAsync"/> só retorna um plano
/// <see cref="PartitionPlanOutcome.Planned"/> com partes — a base do réplay idempotente.
/// <see cref="SaveAsync"/> lança <see cref="PartitionPlanConflictException"/> quando o índice único
/// filtrado de canônicos já foi ocupado por uma corrida concorrente; o chamador deve reler via
/// <see cref="FindCanonicalAsync"/> (nunca tratar como erro de negócio).
/// </summary>
public interface IPartitionPlanStore
{
    /// <summary>Plano canônico atual para o artefato + identidade determinística de entrada, se houver.</summary>
    Task<PartitionPlan?> FindCanonicalAsync(
        TenantScope scope, ArtifactId artifact, Sha256Hash planHash, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve um plano pela sua identidade opaca, estritamente dentro do escopo; <see langword="null"/> se
    /// não existir ou pertencer a outro tenant/projeto (anti-IDOR — nunca revela existência de um plano de
    /// outro escopo). Usado pela execução (Passo 3) para carregar o plano que o chamador pediu para executar.
    /// </summary>
    Task<PartitionPlan?> FindByIdAsync(TenantScope scope, PartitionPlanId id, CancellationToken cancellationToken);

    /// <summary>Persiste um novo plano com suas partes, atomicamente (append-only).</summary>
    /// <exception cref="PartitionPlanConflictException">Corrida concorrente já gravou o canônico equivalente.</exception>
    Task<PartitionPlan> SaveAsync(PartitionPlan plan, CancellationToken cancellationToken);
}
