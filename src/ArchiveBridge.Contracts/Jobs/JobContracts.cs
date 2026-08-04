using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Operations;

namespace ArchiveBridge.Contracts.Jobs;

/// <summary>Resultado estruturado de um comando de Job (idempotência e fencing explícitos).</summary>
public enum JobCommandOutcome
{
    /// <summary>O efeito foi aplicado agora.</summary>
    Applied,

    /// <summary>O job já estava no estado alvo pela mesma época — replay idempotente e seguro.</summary>
    IdempotentReplay,

    /// <summary>Rejeitado por fencing: owner_worker/lease_epoch defasado.</summary>
    FencedOut,

    /// <summary>Job inexistente no escopo (inclui tentativa cross-tenant barrada pela RLS).</summary>
    NotFound,
}

/// <summary>Comando para criar um Job (sempre carrega escopo de tenant/projeto explícito).</summary>
public sealed record CreateJobCommand(
    TenantScope Scope,
    Workload Workload,
    JobPriority Priority,
    CorrelationId Correlation);

/// <summary>Pedido de reivindicação atômica do próximo Job elegível de um workload.</summary>
public sealed record ClaimRequest(
    TenantScope Scope,
    Workload Workload,
    WorkerId Worker,
    TimeSpan LeaseDuration,
    CorrelationId Correlation);

/// <summary>Comando sob lease (heartbeat/complete): exige job + worker + época para fencing.</summary>
public sealed record LeaseCommand(
    TenantScope Scope,
    JobId JobId,
    WorkerId Worker,
    LeaseEpoch Epoch,
    CorrelationId Correlation);

/// <summary>
/// Cercamento (fencing) de um Job para proteger os EFEITOS de uma operação durável. Diferente do
/// <see cref="LeaseCommand"/> (que transita o Job), este token é levado até a gravação dos efeitos no
/// SQL: a escrita valida, na MESMA transação, que o Job ainda está em Processing, é do mesmo
/// <see cref="Worker"/> e <see cref="Epoch"/>, e o lease continua válido. Assim, um dono defasado
/// (lease expirado/recuperado) não persiste efeito parcial. <see langword="null"/> em invocações
/// não duráveis (sem Job).
/// </summary>
public sealed record JobFence(
    TenantScope Scope,
    JobId Job,
    WorkerId Worker,
    LeaseEpoch Epoch);

/// <summary>Comando de falha: dispõe sobre retry (transitório) vs. terminal via <see cref="RetryDisposition"/>.</summary>
public sealed record FailJobCommand(
    TenantScope Scope,
    JobId JobId,
    WorkerId Worker,
    LeaseEpoch Epoch,
    ErrorCode Error,
    RetryDisposition Disposition,
    CorrelationId Correlation);

/// <summary>Job reivindicado: identidade, época concedida, expiração do lease e número da tentativa.</summary>
public sealed record ClaimedJob(
    JobId JobId,
    LeaseEpoch Epoch,
    DateTimeOffset LeaseExpiresAtUtc,
    int AttemptNumber);

/// <summary>Registro de auditoria de uma transição de estado (sem conteúdo sensível).</summary>
public sealed record JobTransitionRecord(
    JobId JobId,
    JobState? FromState,
    JobState ToState,
    ReasonCode Reason,
    LeaseEpoch LeaseEpoch,
    WorkerId? Worker,
    CorrelationId Correlation,
    DateTimeOffset OccurredAtUtc);
