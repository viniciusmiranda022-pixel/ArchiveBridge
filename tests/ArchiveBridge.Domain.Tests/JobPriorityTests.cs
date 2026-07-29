using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Domain.Tests;

public sealed class JobPriorityTests
{
    [Theory]
    [InlineData(JobPriority.MinValue)]
    [InlineData(50)]
    [InlineData(JobPriority.MaxValue)]
    public void PrioritiesInsideTheRangeAreAccepted(int value)
    {
        Assert.Equal(value, new JobPriority(value).Value);
    }

    [Theory]
    [InlineData(JobPriority.MinValue - 1)]
    [InlineData(JobPriority.MaxValue + 1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void PrioritiesOutsideTheRangeAreRejectedFailClosed(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPriority(value));
    }

    [Fact]
    public void NormalIsTheLowestAndHighestIsTheMaximum()
    {
        Assert.Equal(JobPriority.MinValue, JobPriority.Normal.Value);
        Assert.Equal(JobPriority.MaxValue, JobPriority.Highest.Value);
    }
}
