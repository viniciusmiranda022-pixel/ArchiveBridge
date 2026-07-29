using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Tests;

public sealed class JobTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static Job NewJob() =>
        Job.Create(JobId.New(), new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), Workload.Pst, JobPriority.Normal, Now);

    [Fact]
    public void CreatedJobStartsPendingAndClaimable()
    {
        var job = NewJob();

        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(LeaseEpoch.Initial, job.LeaseEpoch);
        Assert.True(job.IsClaimable(Now));
    }

    [Fact]
    public void ClaimMovesToProcessingAndIncrementsEpochAndAttempt()
    {
        var job = NewJob();

        var transition = job.Claim(new WorkerId("w"), Now, Lease);

        Assert.Equal(JobState.Processing, job.State);
        Assert.Equal(1, job.LeaseEpoch.Value);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(Now + Lease, job.LeaseExpiresAtUtc);
        Assert.Equal(ReasonCode.Claimed, transition.Reason);
    }

    [Fact]
    public void ClaimingATerminalJobFailsClosed()
    {
        var job = NewJob();
        job.Claim(new WorkerId("w"), Now, Lease);
        job.Complete(new WorkerId("w"), job.LeaseEpoch, Now);

        Assert.Throws<InvalidJobTransitionException>(() => job.Claim(new WorkerId("w"), Now, Lease));
    }

    [Fact]
    public void CompleteWithWrongEpochIsFencedOut()
    {
        var job = NewJob();
        job.Claim(new WorkerId("w"), Now, Lease);

        Assert.Throws<InvalidJobTransitionException>(
            () => job.Complete(new WorkerId("w"), new LeaseEpoch(999), Now));
    }

    [Fact]
    public void CompleteFromWrongWorkerIsFencedOut()
    {
        var job = NewJob();
        job.Claim(new WorkerId("owner"), Now, Lease);

        Assert.Throws<InvalidJobTransitionException>(
            () => job.Complete(new WorkerId("intruder"), job.LeaseEpoch, Now));
    }

    [Fact]
    public void ScheduleRetryReturnsToQueueAndClearsOwner()
    {
        var job = NewJob();
        job.Claim(new WorkerId("w"), Now, Lease);
        var next = Now + TimeSpan.FromMinutes(1);

        job.ScheduleRetry(new WorkerId("w"), job.LeaseEpoch, ErrorCode.TransientProvider, next, Now);

        Assert.Equal(JobState.RetryScheduled, job.State);
        Assert.Null(job.OwnerWorker);
        Assert.Equal(next, job.NextAttemptAtUtc);
        Assert.False(job.IsClaimable(Now));
        Assert.True(job.IsClaimable(next));
    }

    [Fact]
    public void RecoverExpiredLeaseRequeuesWhenAttemptsRemain()
    {
        var job = NewJob();
        job.Claim(new WorkerId("w"), Now, Lease);
        var later = Now + Lease + TimeSpan.FromSeconds(1);

        var transition = job.RecoverExpiredLease(later, RetryPolicy.Default);

        Assert.Equal(JobState.RetryScheduled, job.State);
        Assert.Equal(ReasonCode.LeaseExpiredRecovered, transition.Reason);
        Assert.Null(job.OwnerWorker);
    }

    [Fact]
    public void RecoverExpiredLeaseFailsWhenAttemptsExhausted()
    {
        var job = NewJob();
        job.Claim(new WorkerId("w"), Now, Lease);
        var later = Now + Lease + TimeSpan.FromSeconds(1);

        var transition = job.RecoverExpiredLease(later, new RetryPolicy(1, Lease, Lease));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ReasonCode.AttemptsExhausted, transition.Reason);
    }

    [Fact]
    public void RecoveringANonExpiredLeaseFailsClosed()
    {
        var job = NewJob();
        job.Claim(new WorkerId("w"), Now, Lease);

        Assert.Throws<InvalidJobTransitionException>(() => job.RecoverExpiredLease(Now, RetryPolicy.Default));
    }

    [Fact]
    public void CancelFromPendingIsAllowedButFromTerminalFailsClosed()
    {
        var job = NewJob();
        job.Cancel(Now);
        Assert.Equal(JobState.Cancelled, job.State);

        Assert.Throws<InvalidJobTransitionException>(() => job.Cancel(Now));
    }
}
