namespace ArchiveBridge.Domain.Jobs;

public readonly record struct JobId(Guid Value);

/// <summary>Job durável (skeleton). Fencing por owner_worker + lease_epoch (ADR-0003).</summary>
public sealed class Job(JobId id, JobState state)
{
    public JobId Id { get; } = id;
    public JobState State { get; private set; } = state;
}
