using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Domain.Tests;

public sealed class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void ShouldRetryRespectsMaxAttempts(int attemptCount, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.Default.ShouldRetry(attemptCount));
    }

    [Fact]
    public void NextAttemptGrowsExponentiallyAndIsCapped()
    {
        var policy = new RetryPolicy(10, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(80));

        Assert.Equal(Now.AddSeconds(10), policy.NextAttempt(1, Now));
        Assert.Equal(Now.AddSeconds(20), policy.NextAttempt(2, Now));
        Assert.Equal(Now.AddSeconds(40), policy.NextAttempt(3, Now));
        Assert.Equal(Now.AddSeconds(80), policy.NextAttempt(4, Now));
        Assert.Equal(Now.AddSeconds(80), policy.NextAttempt(5, Now)); // limitado ao teto
    }
}
