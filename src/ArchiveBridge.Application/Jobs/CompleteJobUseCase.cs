using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>Conclui um Job sob fencing (Processing → Completed). Replay é idempotente.</summary>
public sealed class CompleteJobUseCase(IJobStore store)
{
    private readonly IJobStore _store = store;

    /// <summary>Conclui o Job; resultado explícito (Applied/IdempotentReplay/FencedOut/NotFound).</summary>
    public Task<JobCommandOutcome> ExecuteAsync(LeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _store.CompleteAsync(command, cancellationToken);
    }
}
