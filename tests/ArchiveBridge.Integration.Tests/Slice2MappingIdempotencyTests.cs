using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B4: a idempotência do mapping usa a IMPRESSÃO DIGITAL COMPLETA. Mesmos parâmetros ⇒ reaproveita a
/// versão e devolve o artefato/evidência EXATOS (mesmo SHA-256). Code page diferente ⇒ nova versão.
/// O documento devolvido nunca traz evidência de outra versão.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2MappingIdempotencyTests(SqlServerFixture fixture)
{
    private async Task<(TenantScope Scope, WaveId WaveId)> ApprovedWaveAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var store = Slice2Support.WaveStore(fixture);
        await store.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await store.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    [Fact]
    public async Task SameFingerprintReusesVersionAndReturnsExactArtifact()
    {
        var (scope, waveId) = await ApprovedWaveAsync();
        var useCase = Slice2Support.GenerateMapping(fixture, new MutableClock(Slice2Support.Now));

        var first = await useCase.ExecuteAsync(scope, waveId, new ContentCodePage(1252), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, waveId, new ContentCodePage(1252), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);

        Assert.True(first.Regenerated);
        Assert.False(second.Regenerated);
        Assert.Equal(1, second.Version.Version.Value);
        // O documento devolvido no reaproveitamento tem o MESMO SHA-256 da evidência daquela versão.
        Assert.Equal(second.Version.ContentSha256, second.Document.ContentSha256);
        Assert.Equal(first.Document.ContentSha256, second.Document.ContentSha256);
    }

    [Fact]
    public async Task DifferentCodePageRegeneratesNewVersion()
    {
        var (scope, waveId) = await ApprovedWaveAsync();
        var useCase = Slice2Support.GenerateMapping(fixture, new MutableClock(Slice2Support.Now));

        var v1 = await useCase.ExecuteAsync(scope, waveId, new ContentCodePage(1252), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);
        var v2 = await useCase.ExecuteAsync(scope, waveId, new ContentCodePage(65001), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);

        Assert.True(v2.Regenerated);
        Assert.Equal(1, v1.Version.Version.Value);
        Assert.Equal(2, v2.Version.Version.Value);
        Assert.NotEqual(v1.Document.ContentSha256, v2.Document.ContentSha256);
        Assert.Equal(v2.Version.ContentSha256, v2.Document.ContentSha256);
    }
}
