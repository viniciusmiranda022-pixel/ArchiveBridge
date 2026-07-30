using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>Renova (heartbeat) o lease do worker titular; um worker antigo é barrado por fencing.</summary>
public sealed class RenewJobLeaseUseCase(IJobLeaseManager leaseManager)
{
    private readonly IJobLeaseManager _leaseManager = leaseManager;

    /// <summary>Renova o lease; <see cref="JobCommandOutcome.FencedOut"/> se a época estiver defasada.</summary>
    public Task<JobCommandOutcome> ExecuteAsync(LeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _leaseManager.RenewAsync(command, cancellationToken);
    }
}
