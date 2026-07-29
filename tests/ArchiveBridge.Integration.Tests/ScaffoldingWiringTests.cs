using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Infrastructure.Jobs;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Prova a composição ports/adapters do scaffolding — e documenta, de forma honesta, que o
/// adapter é um placeholder NÃO funcional (lança <see cref="NotImplementedException"/>).
/// Não há capacidade funcional de migração.
/// </summary>
public sealed class ScaffoldingWiringTests
{
    [Fact]
    public void InfrastructureImplementsTheJobLeaseSourcePort()
    {
        Assert.True(typeof(IJobLeaseSource).IsAssignableFrom(typeof(NotImplementedJobLeaseSource)));
    }

    [Fact]
    public async Task ScaffoldingLeaseSourceIsNotFunctionalYet()
    {
        var source = new NotImplementedJobLeaseSource();

        await Assert.ThrowsAsync<NotImplementedException>(
            () => source.TryClaimNextAsync(Workload.Upload, CancellationToken.None));
    }
}
