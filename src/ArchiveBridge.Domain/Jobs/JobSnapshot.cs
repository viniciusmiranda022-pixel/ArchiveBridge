using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Estado durável completo de um Job, usado para reidratar o agregado a partir do SQL Server
/// (<see cref="Job.Rehydrate"/>) e para expor uma visão de leitura. Reflete o que está persistido.
/// </summary>
public sealed record JobSnapshot(
    JobId Id,
    TenantId Tenant,
    ProjectId Project,
    Workload Workload,
    JobPriority Priority,
    JobState State,
    WorkerId? OwnerWorker,
    LeaseEpoch LeaseEpoch,
    DateTimeOffset? LeaseExpiresAtUtc,
    int AttemptCount,
    DateTimeOffset? NextAttemptAtUtc,
    ErrorCode? LastErrorCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
