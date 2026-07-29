using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.Tests;

public sealed class JobLeaseTests
{
    [Fact]
    public void JobLeaseCarriesLeaseEpochForFencing()
    {
        var lease = new JobLease(new JobId(Guid.NewGuid()), JobState.Processing, LeaseEpoch: 7);

        Assert.Equal(7, lease.LeaseEpoch);
        Assert.Equal(JobState.Processing, lease.State);
    }
}
