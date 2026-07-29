using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Domain.Tests;

public sealed class JobTests
{
    [Fact]
    public void NewJobKeepsInitialState()
    {
        var job = new Job(new JobId(Guid.NewGuid()), JobState.Pending);

        Assert.Equal(JobState.Pending, job.State);
    }

    [Fact]
    public void JobIdEqualityIsValueBased()
    {
        var value = Guid.NewGuid();

        Assert.Equal(new JobId(value), new JobId(value));
    }
}
