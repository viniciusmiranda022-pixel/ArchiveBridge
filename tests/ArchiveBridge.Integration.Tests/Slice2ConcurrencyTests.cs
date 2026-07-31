using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B6: concorrência otimista por <c>row_version</c>. Dois leitores concorrentes: o primeiro grava, o
/// segundo (com token defasado) falha com <see cref="ConcurrencyException"/> em vez de sobrescrever
/// (lost update). A sequência de versão do mapping é atômica sob lock.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2ConcurrencyTests(SqlServerFixture fixture)
{
    private static readonly ContentCodePage CodePage = new(1252);

    private async Task<TenantScope> SeedProjectAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        return scope;
    }

    private async Task<(TenantScope Scope, WaveId WaveId)> SeedWaveAsync(bool approved = false)
    {
        var scope = await SeedProjectAsync();
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        if (approved)
        {
            Slice2Support.Approve(wave);
        }

        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    [Fact]
    public async Task ConcurrentWaveStatusSaveDetectsConflict()
    {
        var (scope, waveId) = await SeedWaveAsync();
        var store = Slice2Support.WaveStore(fixture);
        var first = await store.GetAsync(scope, waveId, CancellationToken.None);
        var second = await store.GetAsync(scope, waveId, CancellationToken.None);

        first!.StartValidation();
        await store.SaveStatusAsync(first, CorrelationId.New(), CancellationToken.None);

        second!.StartValidation();
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.SaveStatusAsync(second, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentSelectionChangeDetectsConflict()
    {
        var (scope, waveId) = await SeedWaveAsync();
        var store = Slice2Support.WaveStore(fixture);
        var first = await store.GetAsync(scope, waveId, CancellationToken.None);
        var second = await store.GetAsync(scope, waveId, CancellationToken.None);

        first!.UpdateSelection(new WaveSelection([Slice2Support.Entry("b.pst", "v@contoso.com", 20)]));
        await store.SaveSelectionAsync(first, CorrelationId.New(), CancellationToken.None);

        second!.UpdateSelection(new WaveSelection([Slice2Support.Entry("c.pst", "w@contoso.com", 30)]));
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.SaveSelectionAsync(second, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentConfigChangeDetectsConflict()
    {
        var scope = await SeedProjectAsync();
        var store = Slice2Support.ProjectStore(fixture);
        var first = await store.GetAsync(scope, CancellationToken.None);
        var second = await store.GetAsync(scope, CancellationToken.None);

        first!.UpdateConfiguration(new ProjectConfiguration(new TargetTenant("fabrikam.onmicrosoft.com"), TargetArchivePolicy.PrimaryMailbox), Slice2Support.Now);
        await store.SaveConfigurationAsync(first, CorrelationId.New(), CancellationToken.None);

        second!.UpdateConfiguration(new ProjectConfiguration(new TargetTenant("northwind.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive), Slice2Support.Now);
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.SaveConfigurationAsync(second, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentFreezeDetectsConflict()
    {
        var (scope, waveId) = await SeedWaveAsync(approved: true);
        var store = Slice2Support.WaveStore(fixture);
        var first = await store.GetAsync(scope, waveId, CancellationToken.None);
        var second = await store.GetAsync(scope, waveId, CancellationToken.None);

        first!.Freeze();
        await store.SaveStatusAsync(first, CorrelationId.New(), CancellationToken.None);

        second!.Freeze();
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.SaveStatusAsync(second, CorrelationId.New(), CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentMappingGenerationSequencesVersionsAtomically()
    {
        var (scope, waveId) = await SeedWaveAsync(approved: true);
        var wave = await Slice2Support.WaveStore(fixture).GetAsync(scope, waveId, CancellationToken.None);
        var store = Slice2Support.MappingStore(fixture);

        async Task<int> GenerateAsync()
        {
            var result = MappingCsvGenerator.Generate(wave!, CodePage, MappingPolicy.Default, MappingVersion.Initial, "do", Slice2Support.Now);
            var persisted = await store.SaveAsync(scope, result, CancellationToken.None);
            return persisted.Version.Value;
        }

        var versions = await Task.WhenAll(GenerateAsync(), GenerateAsync());
        Assert.Equal([1, 2], versions.OrderBy(v => v).ToArray());
    }
}
