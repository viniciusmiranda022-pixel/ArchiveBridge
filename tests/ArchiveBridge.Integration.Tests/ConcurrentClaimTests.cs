using System.Collections.Concurrent;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ConcurrentClaimTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task TwoWorkersClaimingOneJobProduceExactlyOneWinner()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        async Task<ClaimedJob?> ClaimAs(string worker) =>
            await store.TryClaimNextAsync(
                new ClaimRequest(scope, Workload.Pst, new WorkerId(worker), Lease, CorrelationId.New()),
                CancellationToken.None);

        var results = await Task.WhenAll(ClaimAs("worker-a"), ClaimAs("worker-b"));

        var winners = results.Where(result => result is not null).ToList();
        Assert.Single(winners);
        Assert.Equal(jobId, winners[0]!.JobId);
    }

    [Fact]
    public async Task ManyWorkersNeverClaimTheSameJobTwice()
    {
        const int jobCount = 40;
        const int workerCount = 8;
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);

        for (var i = 0; i < jobCount; i++)
        {
            await store.CreateAsync(
                new CreateJobCommand(scope, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
                CancellationToken.None);
        }

        var claimed = new ConcurrentBag<Guid>();

        async Task DrainAs(string worker)
        {
            while (true)
            {
                var job = await store.TryClaimNextAsync(
                    new ClaimRequest(scope, Workload.Upload, new WorkerId(worker), Lease, CorrelationId.New()),
                    CancellationToken.None);
                if (job is null)
                {
                    return;
                }

                claimed.Add(job.JobId.Value);
                await store.CompleteAsync(
                    new LeaseCommand(scope, job.JobId, new WorkerId(worker), job.Epoch, CorrelationId.New()),
                    CancellationToken.None);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(i => DrainAs($"worker-{i}")));

        Assert.Equal(jobCount, claimed.Count);
        Assert.Equal(jobCount, claimed.Distinct().Count());
    }
}
