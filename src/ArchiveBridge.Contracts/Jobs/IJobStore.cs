using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.Jobs;

/// <summary>
/// Porta da fila durável de Jobs (ADR-0003). Implementada sobre SQL Server on-premises: o claim é
/// atômico (um único vencedor), e complete/fail são cercados por (owner_worker + lease_epoch).
/// Cada operação carrega <see cref="TenantScope"/> explícito; a RLS por SESSION_CONTEXT impede
/// acesso cruzado entre tenants. Não é um repositório genérico universal — expõe apenas as
/// operações do ciclo de vida.
/// </summary>
public interface IJobStore
{
    /// <summary>Cria um Job em <c>Pending</c> e registra a transição inicial na auditoria.</summary>
    Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Reivindica atomicamente o próximo Job elegível do workload (ordem anti-starvation),
    /// incrementando a época do lease. Retorna <see langword="null"/> quando não há trabalho.
    /// </summary>
    Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken);

    /// <summary>Lê o estado durável de um Job dentro do escopo; <see langword="null"/> se inexistente/cross-tenant.</summary>
    Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken);

    /// <summary>Conclui o Job (Processing → Completed) sob fencing. Idempotente por época.</summary>
    Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken);

    /// <summary>Marca falha definitiva (Processing → Failed) sob fencing. Idempotente por época.</summary>
    Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken);

    /// <summary>
    /// Agenda nova tentativa (Processing → RetryScheduled) sob fencing, definindo
    /// <paramref name="nextAttemptAtUtc"/>. Idempotente por época.
    /// </summary>
    Task<JobCommandOutcome> ScheduleRetryAsync(
        LeaseCommand command,
        ErrorCode errorCode,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Solicita retry manual autorizado (ação de controle do Portal — Passo 7): elegível SOMENTE quando o
    /// Job está em <see cref="JobState.RetryScheduled"/>; adianta <c>next_attempt_at_utc</c> para agora,
    /// sem alterar estado, tentativas, erro ou lease. Sem fencing por worker (não há lease ativo neste
    /// estado). Idempotente pela <paramref name="idempotencyKey"/> informada: replay da MESMA chave para o
    /// MESMO Job é <see cref="JobRetryRequestOutcome.IdempotentReplay"/>; a MESMA chave para um Job
    /// DIFERENTE é <see cref="JobRetryRequestOutcome.IdempotencyConflict"/> (backstop por índice único
    /// filtrado). Job inexistente/cross-tenant/cross-project no escopo é
    /// <see cref="JobRetryRequestOutcome.NotFound"/>; existente mas não elegível é
    /// <see cref="JobRetryRequestOutcome.NotEligible"/>.
    /// </summary>
    Task<JobRetryRequestOutcome> RequestManualRetryAsync(
        TenantScope scope,
        JobId jobId,
        Guid idempotencyKey,
        CorrelationId correlation,
        CancellationToken cancellationToken);
}
