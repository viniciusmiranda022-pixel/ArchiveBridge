using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Contracts.PstProcessing;

/// <summary>
/// Persistência append-only das execuções de partição (checkpoint durável do Passo 3 — o manifesto do
/// <c>PartCreated</c>). Tenant/projeto scoped (RLS + filtro explícito por projeto). Diferente das stores de
/// inspeção/planejamento, esta store nunca grava tentativas fracassadas: uma linha só existe depois que o
/// output foi materializado, reaberto/reinspecionado e confirmado. <see cref="SaveAsync"/> lança
/// <see cref="PartitionExecutionConflictException"/> quando o índice único de canonicidade (tenant, projeto,
/// plano, parte) já foi ocupado por uma corrida concorrente; o chamador deve reler via
/// <see cref="FindCanonicalAsync"/> (nunca tratar como erro de negócio).
/// </summary>
public interface IPartitionExecutionStore
{
    /// <summary>Execução canônica atual para o plano+parte, se houver — a base do réplay idempotente.</summary>
    Task<PartitionExecutionRecord?> FindCanonicalAsync(
        TenantScope scope, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken);

    /// <summary>Persiste uma nova execução concluída e verificada (append-only).</summary>
    /// <exception cref="PartitionExecutionConflictException">Corrida concorrente já gravou o canônico equivalente.</exception>
    Task<PartitionExecutionRecord> SaveAsync(PartitionExecutionRecord execution, CancellationToken cancellationToken);
}
