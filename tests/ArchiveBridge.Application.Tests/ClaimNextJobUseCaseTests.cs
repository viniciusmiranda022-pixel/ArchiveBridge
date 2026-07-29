using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Prova que a Application é testável apenas com Domain + Contracts, através de um duplo de teste
/// da porta <see cref="IJobLeaseSource"/> — sem referenciar nenhuma implementação de Infrastructure.
/// </summary>
public sealed class ClaimNextJobUseCaseTests
{
    private sealed class StubLeaseSource(JobLease? result) : IJobLeaseSource
    {
        public Workload? LastWorkload { get; private set; }

        public Task<JobLease?> TryClaimNextAsync(Workload workload, CancellationToken cancellationToken)
        {
            LastWorkload = workload;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ExecuteForwardsWorkloadAndReturnsLease()
    {
        var expected = new JobLease(new JobId(Guid.NewGuid()), JobState.Pending, LeaseEpoch: 1);
        var stub = new StubLeaseSource(expected);
        var useCase = new ClaimNextJobUseCase(stub);

        var actual = await useCase.ExecuteAsync(Workload.EnterpriseVault, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(Workload.EnterpriseVault, stub.LastWorkload);
    }

    [Fact]
    public async Task ExecuteReturnsNullWhenThereIsNoWork()
    {
        var useCase = new ClaimNextJobUseCase(new StubLeaseSource(null));

        var actual = await useCase.ExecuteAsync(Workload.Pst, CancellationToken.None);

        Assert.Null(actual);
    }
}
