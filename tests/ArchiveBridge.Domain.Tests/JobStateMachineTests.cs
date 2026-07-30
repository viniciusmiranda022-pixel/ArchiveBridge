using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Domain.Tests;

public sealed class JobStateMachineTests
{
    [Theory]
    [InlineData(JobState.Pending, JobState.Processing)]
    [InlineData(JobState.Pending, JobState.Cancelled)]
    [InlineData(JobState.Processing, JobState.Completed)]
    [InlineData(JobState.Processing, JobState.Failed)]
    [InlineData(JobState.Processing, JobState.RetryScheduled)]
    [InlineData(JobState.RetryScheduled, JobState.Processing)]
    public void AllowedTransitionsArePermitted(JobState from, JobState to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(JobState.Pending, JobState.Completed)]
    [InlineData(JobState.Completed, JobState.Processing)]
    [InlineData(JobState.Failed, JobState.Processing)]
    [InlineData(JobState.Cancelled, JobState.Processing)]
    [InlineData(JobState.Processing, JobState.Pending)]
    public void DisallowedTransitionsAreRejected(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidJobTransitionException>(() => JobStateMachine.EnsureCanTransition(from, to));
    }

    [Theory]
    [InlineData(JobState.Completed, true)]
    [InlineData(JobState.Failed, true)]
    [InlineData(JobState.Cancelled, true)]
    [InlineData(JobState.Pending, false)]
    [InlineData(JobState.Processing, false)]
    [InlineData(JobState.RetryScheduled, false)]
    public void TerminalStatesAreClassifiedCorrectly(JobState state, bool expected)
    {
        Assert.Equal(expected, JobStateMachine.IsTerminal(state));
    }
}
