using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.Jobs;

/// <summary>
/// Concessão de um job reivindicado: identidade, estado e época de lease para fencing
/// (owner_worker + lease_epoch, ADR-0003). DTO de contrato — sem tipos de infraestrutura.
/// </summary>
public readonly record struct JobLease(JobId JobId, JobState State, long LeaseEpoch);

/// <summary>
/// Porta da fila durável (ADR-0003): fonte de onde os workers reivindicam a próxima concessão
/// de job. Implementada na Infrastructure (SQL Server local); o domínio e a aplicação não
/// conhecem o mecanismo de persistência.
/// </summary>
public interface IJobLeaseSource
{
    /// <summary>
    /// Tenta reivindicar o próximo job elegível de um workload, com fencing. Retorna
    /// <see langword="null"/> quando não há trabalho elegível — nunca lança para "fila vazia".
    /// </summary>
    Task<JobLease?> TryClaimNextAsync(Workload workload, CancellationToken cancellationToken);
}
