using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B8: uma onda cuja seleção contém archive com identidade NÃO resolvida (derivada só da mailbox,
/// sem manifesto autorizado) é recusada na validação (fail-closed); a resolução é preservada no
/// round-trip pelo banco. Identidade resolvida valida normalmente.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2UnresolvedArchiveTests(SqlServerFixture fixture)
{
    private async Task<(TenantScope Scope, WaveId WaveId)> SeedAsync(WaveSelection selection)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, selection);
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    [Fact]
    public async Task UnresolvedArchiveBlocksValidation()
    {
        var (scope, waveId) = await SeedAsync(new WaveSelection([Slice2Support.UnresolvedEntry("a.pst", "u@contoso.com", 10)]));
        await Assert.ThrowsAsync<PlanningValidationException>(() =>
            Slice2Support.ValidateWave(fixture, new MutableClock(Slice2Support.Now))
                .ExecuteAsync(scope, waveId, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task ResolvedArchiveValidatesNormally()
    {
        var (scope, waveId) = await SeedAsync(new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        var result = await Slice2Support.ValidateWave(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, waveId, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(WaveStatus.ReadyForApproval, result.Status);
    }
}
