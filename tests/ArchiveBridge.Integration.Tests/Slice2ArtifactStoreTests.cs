using System.Globalization;
using System.Text;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Mapping;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B3: artefato publicado como CONJUNTO atômico e imutável (staging + rename do diretório). A leitura
/// valida os três arquivos + hash + sha256 + manifesto (escopo/tamanho/caminho) e falha fechada. Nada é
/// sobrescrito; concorrência divergente deixa o original intacto; conjunto incompleto/adulterado é
/// recusado.
/// </summary>
public sealed class Slice2ArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ab_artifact_test_" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemMappingArtifactStore _store;
    private readonly TenantScope _scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private readonly WaveId _wave = WaveId.New();

    public Slice2ArtifactStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new FileSystemMappingArtifactStore(_root);
    }

    private MappingArtifactDescriptor Descriptor(int version) => new(_scope, _wave, new MappingVersion(version));

    private string FinalDir(int version) => Path.Combine(
        _root, _scope.Tenant.Value.ToString("N"), _scope.Project.Value.ToString("N"), _wave.Value.ToString("N"),
        string.Create(CultureInfo.InvariantCulture, $"v{version}"));

    private static (byte[] Bytes, Sha256Hash Hash) Payload(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return (bytes, DeterministicHash.ComputeBytes(bytes));
    }

    private async Task<MappingArtifactReference> PublishAsync(int version, byte[] bytes, Sha256Hash hash)
    {
        var staging = await _store.StageAsync(bytes, hash, CancellationToken.None);
        return await _store.PublishAsync(staging, Descriptor(version), CancellationToken.None);
    }

    [Fact]
    public async Task PublishedSetIsReadableAndValidated()
    {
        var (bytes, hash) = Payload("Workload,FilePath\r\nExchange,/a.pst\r\n");
        var reference = await PublishAsync(1, bytes, hash);

        Assert.Equal(bytes.LongLength, reference.SizeBytes);
        Assert.True(File.Exists(Path.Combine(FinalDir(1), "mapping.csv")));
        Assert.True(File.Exists(Path.Combine(FinalDir(1), "mapping.sha256")));
        Assert.True(File.Exists(Path.Combine(FinalDir(1), "manifest.json")));

        var read = await _store.GetAsync(Descriptor(1), CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(hash, read!.ContentSha256);
        Assert.Equal(bytes, read.Bytes.ToArray());
    }

    [Fact]
    public async Task RepublishSameVersionSameBytesIsIdempotent()
    {
        var (bytes, hash) = Payload("same-bytes");
        var first = await PublishAsync(1, bytes, hash);
        var second = await PublishAsync(1, bytes, hash);
        Assert.Equal(first.LogicalPath, second.LogicalPath);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
    }

    [Fact]
    public async Task RepublishSameVersionDivergentBytesIsRejectedOriginalIntact()
    {
        var (bytes, hash) = Payload("original");
        await PublishAsync(1, bytes, hash);

        var (otherBytes, otherHash) = Payload("tampered");
        await Assert.ThrowsAsync<MappingGenerationException>(() => PublishAsync(1, otherBytes, otherHash));

        var read = await _store.GetAsync(Descriptor(1), CancellationToken.None);
        Assert.Equal(hash, read!.ContentSha256);
    }

    [Fact]
    public async Task PriorVersionRemainsIntactWhenNewVersionPublished()
    {
        var (b1, h1) = Payload("v1-content");
        var (b2, h2) = Payload("v2-content");
        await PublishAsync(1, b1, h1);
        await PublishAsync(2, b2, h2);

        Assert.Equal(h1, (await _store.GetAsync(Descriptor(1), CancellationToken.None))!.ContentSha256);
        Assert.Equal(h2, (await _store.GetAsync(Descriptor(2), CancellationToken.None))!.ContentSha256);
    }

    [Fact]
    public async Task TamperedShaFailsClosed()
    {
        var (bytes, hash) = Payload("content");
        await PublishAsync(1, bytes, hash);
        await File.WriteAllTextAsync(Path.Combine(FinalDir(1), "mapping.sha256"), "0000000000000000000000000000000000000000000000000000000000000000\n");
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task TamperedManifestFailsClosed()
    {
        var (bytes, hash) = Payload("content");
        await PublishAsync(1, bytes, hash);
        await File.WriteAllTextAsync(Path.Combine(FinalDir(1), "manifest.json"), "{\"Tenant\":\"00000000-0000-0000-0000-000000000000\"}");
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task ManifestFromAnotherWaveFailsClosed()
    {
        var (bytes, hash) = Payload("content");
        await PublishAsync(1, bytes, hash);
        // Reescreve o manifesto com uma onda diferente (tamanho/hash corretos, escopo trocado).
        var alien = $"{{\"Tenant\":\"{_scope.Tenant.Value}\",\"Project\":\"{_scope.Project.Value}\",\"Wave\":\"{Guid.NewGuid()}\",\"Version\":1,\"LogicalPath\":\"x\",\"ContentSha256\":\"{hash.Value}\",\"SizeBytes\":{bytes.Length}}}";
        await File.WriteAllTextAsync(Path.Combine(FinalDir(1), "manifest.json"), alien);
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task IncompleteSetFailsClosed()
    {
        var (bytes, hash) = Payload("content");
        await PublishAsync(1, bytes, hash);
        File.Delete(Path.Combine(FinalDir(1), "mapping.csv"));
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task TwoConcurrentDivergentPublicationsOneWinsOtherRejected()
    {
        var (b1, h1) = Payload("worker-A-bytes");
        var (b2, h2) = Payload("worker-B-bytes");
        var stagingA = await _store.StageAsync(b1, h1, CancellationToken.None);
        var stagingB = await _store.StageAsync(b2, h2, CancellationToken.None);

        await _store.PublishAsync(stagingA, Descriptor(1), CancellationToken.None);
        await Assert.ThrowsAsync<MappingGenerationException>(() =>
            _store.PublishAsync(stagingB, Descriptor(1), CancellationToken.None));

        Assert.Equal(h1, (await _store.GetAsync(Descriptor(1), CancellationToken.None))!.ContentSha256);
    }

    [Fact]
    public async Task DiscardedStagingLeavesNoFinalAndCanRegenerate()
    {
        var (bytes, hash) = Payload("content");
        var staging = await _store.StageAsync(bytes, hash, CancellationToken.None);
        await _store.DiscardAsync(staging, CancellationToken.None);

        Assert.False(Directory.Exists(staging.StagingPath));
        Assert.Null(await _store.GetAsync(Descriptor(1), CancellationToken.None));

        // Regeneração após descarte publica normalmente.
        await PublishAsync(1, bytes, hash);
        Assert.NotNull(await _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task MismatchedStageHashIsRejected()
    {
        var (bytes, _) = Payload("content");
        await Assert.ThrowsAsync<MappingGenerationException>(() =>
            _store.StageAsync(bytes, new Sha256Hash("not-the-hash"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingArtifactReadsAsNull() =>
        Assert.Null(await _store.GetAsync(Descriptor(9), CancellationToken.None));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>B3: contenção robusta de caminhos (irmão de prefixo semelhante, rooted, symlink/reparse).</summary>
public sealed class ArtifactPathContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ab_contain_" + Guid.NewGuid().ToString("N"));

    public ArtifactPathContainmentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ContainedPathIsAccepted()
    {
        var contained = ArtifactPathContainment.EnsureContained(_root, Path.Combine(_root, "a", "b"));
        Assert.StartsWith(Path.GetFullPath(_root), contained, StringComparison.Ordinal);
    }

    [Fact]
    public void SiblingWithSimilarPrefixIsRejected()
    {
        var sibling = _root + "_evil";
        Assert.Throws<ArgumentException>(() => ArtifactPathContainment.EnsureContained(_root, sibling));
    }

    [Fact]
    public void ParentEscapeIsRejected() =>
        Assert.Throws<ArgumentException>(() => ArtifactPathContainment.EnsureContained(_root, Path.Combine(_root, "..", "outside")));

    [Fact]
    public void SymlinkThatEscapesRootIsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ab_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Ambiente sem suporte a symlink: cenário não aplicável.
        }

        try
        {
            Assert.Throws<ArgumentException>(() =>
                ArtifactPathContainment.EnsureContained(_root, Path.Combine(link, "child")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
