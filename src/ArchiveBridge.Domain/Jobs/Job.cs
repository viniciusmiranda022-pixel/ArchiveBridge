using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Agregado raiz do ciclo de vida durável de um Job (Vertical Slice 1). Encapsula a máquina de
/// estados (<see cref="JobStateMachine"/>) e o fencing por (owner_worker + lease_epoch).
/// Transições e operações inválidas falham de forma fechada (<see cref="InvalidJobTransitionException"/>);
/// não há setters públicos. A fonte de verdade durável é o SQL Server — este agregado expressa
/// as regras usadas nos testes unitários e espelhadas pelos comandos SQL atômicos.
/// </summary>
public sealed class Job
{
    private Job(
        JobId id,
        TenantId tenant,
        ProjectId project,
        Workload workload,
        JobPriority priority,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        Workload = workload;
        Priority = priority;
        State = JobState.Pending;
        LeaseEpoch = LeaseEpoch.Initial;
        AttemptCount = 0;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = createdAtUtc;
    }

    public JobId Id { get; }

    public TenantId Tenant { get; }

    public ProjectId Project { get; }

    public Workload Workload { get; }

    public JobPriority Priority { get; }

    public JobState State { get; private set; }

    public WorkerId? OwnerWorker { get; private set; }

    public LeaseEpoch LeaseEpoch { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public ErrorCode? LastErrorCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Cria um Job novo em <see cref="JobState.Pending"/>, elegível a partir de agora.</summary>
    public static Job Create(
        JobId id,
        TenantId tenant,
        ProjectId project,
        Workload workload,
        JobPriority priority,
        DateTimeOffset now) =>
        new(id, tenant, project, workload, priority, now);

    /// <summary>
    /// Reconstitui um Job a partir do estado persistido (usado pela camada de infraestrutura ao
    /// materializar linhas do SQL Server). Não valida transições — reflete o estado já durável.
    /// </summary>
    public static Job Rehydrate(JobSnapshot snapshot)
    {
        var job = new Job(
            snapshot.Id,
            snapshot.Tenant,
            snapshot.Project,
            snapshot.Workload,
            snapshot.Priority,
            snapshot.CreatedAtUtc)
        {
            State = snapshot.State,
            OwnerWorker = snapshot.OwnerWorker,
            LeaseEpoch = snapshot.LeaseEpoch,
            LeaseExpiresAtUtc = snapshot.LeaseExpiresAtUtc,
            AttemptCount = snapshot.AttemptCount,
            NextAttemptAtUtc = snapshot.NextAttemptAtUtc,
            LastErrorCode = snapshot.LastErrorCode,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
        };
        return job;
    }

    /// <summary>Elegível para reivindicação: Pending/RetryScheduled com <c>NextAttemptAt</c> vencido.</summary>
    public bool IsClaimable(DateTimeOffset now) =>
        State is JobState.Pending or JobState.RetryScheduled
        && (NextAttemptAtUtc is null || NextAttemptAtUtc <= now);

    /// <summary>Reivindica o Job para um worker, incrementando a época do lease (fencing).</summary>
    public JobTransition Claim(WorkerId worker, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (!IsClaimable(now))
        {
            throw InvalidJobTransitionException.Between(State, JobState.Processing);
        }

        var from = State;
        State = JobState.Processing;
        OwnerWorker = worker;
        LeaseEpoch = LeaseEpoch.Next();
        LeaseExpiresAtUtc = now + leaseDuration;
        NextAttemptAtUtc = null;
        AttemptCount++;
        UpdatedAtUtc = now;
        return new JobTransition(Id, from, State, ReasonCode.Claimed, LeaseEpoch, worker, now);
    }

    /// <summary>Renova o lease do worker titular (fencing por owner + época). Não é transição de estado.</summary>
    public void RenewLease(WorkerId worker, LeaseEpoch epoch, DateTimeOffset now, TimeSpan leaseDuration)
    {
        EnsureLeaseHeldBy(worker, epoch);
        LeaseExpiresAtUtc = now + leaseDuration;
        UpdatedAtUtc = now;
    }

    /// <summary>Conclui o Job (Processing → Completed), sob fencing.</summary>
    public JobTransition Complete(WorkerId worker, LeaseEpoch epoch, DateTimeOffset now)
    {
        EnsureLeaseHeldBy(worker, epoch);
        JobStateMachine.EnsureCanTransition(State, JobState.Completed);
        var from = State;
        State = JobState.Completed;
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = now;
        return new JobTransition(Id, from, State, ReasonCode.Completed, epoch, worker, now);
    }

    /// <summary>Marca falha definitiva (Processing → Failed), sob fencing.</summary>
    public JobTransition Fail(WorkerId worker, LeaseEpoch epoch, ErrorCode error, DateTimeOffset now)
    {
        EnsureLeaseHeldBy(worker, epoch);
        JobStateMachine.EnsureCanTransition(State, JobState.Failed);
        var from = State;
        State = JobState.Failed;
        LastErrorCode = error;
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = now;
        return new JobTransition(Id, from, State, ReasonCode.Failed, epoch, worker, now);
    }

    /// <summary>Agenda nova tentativa após falha transitória (Processing → RetryScheduled), sob fencing.</summary>
    public JobTransition ScheduleRetry(
        WorkerId worker,
        LeaseEpoch epoch,
        ErrorCode error,
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset now)
    {
        EnsureLeaseHeldBy(worker, epoch);
        JobStateMachine.EnsureCanTransition(State, JobState.RetryScheduled);
        var from = State;
        State = JobState.RetryScheduled;
        LastErrorCode = error;
        OwnerWorker = null;
        LeaseExpiresAtUtc = null;
        NextAttemptAtUtc = nextAttemptAtUtc;
        UpdatedAtUtc = now;
        return new JobTransition(Id, from, State, ReasonCode.RetryScheduled, epoch, worker, now);
    }

    /// <summary>Cancela um Job não terminal (ação de controle, não sujeita a fencing por worker).</summary>
    public JobTransition Cancel(DateTimeOffset now)
    {
        JobStateMachine.EnsureCanTransition(State, JobState.Cancelled);
        var from = State;
        var epoch = LeaseEpoch;
        var previousOwner = OwnerWorker;
        State = JobState.Cancelled;
        OwnerWorker = null;
        LeaseExpiresAtUtc = null;
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now;
        return new JobTransition(Id, from, State, ReasonCode.Cancelled, epoch, previousOwner, now);
    }

    /// <summary>
    /// Recupera um lease expirado (reaper): Processing com lease vencido volta a RetryScheduled
    /// (se ainda há tentativas) ou Failed (tentativas esgotadas). O worker antigo, com época
    /// defasada, é automaticamente barrado pelo fencing em qualquer operação subsequente.
    /// </summary>
    public JobTransition RecoverExpiredLease(DateTimeOffset now, RetryPolicy policy)
    {
        if (State != JobState.Processing || LeaseExpiresAtUtc is null || LeaseExpiresAtUtc > now)
        {
            throw new InvalidJobTransitionException(
                $"Recuperação inválida: o job {Id.Value} não está com lease expirado em Processing.");
        }

        var from = State;
        var epoch = LeaseEpoch;
        var previousOwner = OwnerWorker;
        OwnerWorker = null;
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = now;

        if (policy.ShouldRetry(AttemptCount))
        {
            State = JobState.RetryScheduled;
            NextAttemptAtUtc = policy.NextAttempt(AttemptCount, now);
            return new JobTransition(Id, from, State, ReasonCode.LeaseExpiredRecovered, epoch, previousOwner, now);
        }

        State = JobState.Failed;
        LastErrorCode = ErrorCode.ResourceExhaustion;
        return new JobTransition(Id, from, State, ReasonCode.AttemptsExhausted, epoch, previousOwner, now);
    }

    private void EnsureLeaseHeldBy(WorkerId worker, LeaseEpoch epoch)
    {
        if (State != JobState.Processing || OwnerWorker != worker || LeaseEpoch != epoch)
        {
            throw new InvalidJobTransitionException(
                $"Operação rejeitada por fencing: o job {Id.Value} não está sob o lease informado " +
                "(owner_worker + lease_epoch).");
        }
    }
}
