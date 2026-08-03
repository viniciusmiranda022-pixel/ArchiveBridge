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
/// B5: armazenamento IMUTÁVEL de artefatos em filesystem. Publicação atômica e versionada; leitura de
/// volta com integridade; republicar a mesma versão só com bytes idênticos (idempotente); bytes
/// divergentes recusados; versões anteriores permanecem intactas.
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

    private MappingArtifactDescriptor Descriptor(int version) =>
        new(_scope, _wave, new MappingVersion(version));

    private static (byte[] Bytes, Sha256Hash Hash) Payload(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return (bytes, DeterministicHash.ComputeBytes(bytes));
    }

    [Fact]
    public async Task ArtifactPublishedAndReadableWithMatchingHash()
    {
        var (bytes, hash) = Payload("Workload,FilePath\r\nExchange,/a.pst\r\n");
        var reference = await _store.SaveAsync(Descriptor(1), bytes, hash, CancellationToken.None);

        Assert.Equal(bytes.LongLength, reference.SizeBytes);
        var read = await _store.GetAsync(reference.LogicalPath, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(hash, read!.ContentSha256);
        Assert.Equal(bytes, read.Bytes.ToArray());
    }

    [Fact]
    public async Task RepublishSameVersionSameBytesIsIdempotent()
    {
        var (bytes, hash) = Payload("same-bytes");
        var first = await _store.SaveAsync(Descriptor(1), bytes, hash, CancellationToken.None);
        var second = await _store.SaveAsync(Descriptor(1), bytes, hash, CancellationToken.None);
        Assert.Equal(first.LogicalPath, second.LogicalPath);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
    }

    [Fact]
    public async Task RepublishSameVersionDivergentBytesIsRejectedAndOriginalIntact()
    {
        var (bytes, hash) = Payload("original");
        await _store.SaveAsync(Descriptor(1), bytes, hash, CancellationToken.None);

        var (otherBytes, otherHash) = Payload("tampered");
        await Assert.ThrowsAsync<MappingGenerationException>(() =>
            _store.SaveAsync(Descriptor(1), otherBytes, otherHash, CancellationToken.None));

        var read = await _store.GetAsync(Descriptor(1).ToLogicalPath(), CancellationToken.None);
        Assert.Equal(hash, read!.ContentSha256);
    }

    [Fact]
    public async Task PriorVersionRemainsIntactWhenNewVersionPublished()
    {
        var (b1, h1) = Payload("v1-content");
        var (b2, h2) = Payload("v2-content");
        var r1 = await _store.SaveAsync(Descriptor(1), b1, h1, CancellationToken.None);
        var r2 = await _store.SaveAsync(Descriptor(2), b2, h2, CancellationToken.None);

        Assert.NotEqual(r1.LogicalPath, r2.LogicalPath);
        Assert.Equal(h1, (await _store.GetAsync(r1.LogicalPath, CancellationToken.None))!.ContentSha256);
        Assert.Equal(h2, (await _store.GetAsync(r2.LogicalPath, CancellationToken.None))!.ContentSha256);
    }

    [Fact]
    public async Task MismatchedHashIsRejected()
    {
        var (bytes, _) = Payload("content");
        await Assert.ThrowsAsync<MappingGenerationException>(() =>
            _store.SaveAsync(Descriptor(1), bytes, new Sha256Hash("not-the-hash"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingArtifactReadsAsNull() =>
        Assert.Null(await _store.GetAsync(Descriptor(9).ToLogicalPath(), CancellationToken.None));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

internal static class ArtifactDescriptorTestExtensions
{
    /// <summary>Reproduz o caminho lógico determinístico do artefato (para asserções de leitura).</summary>
    public static string ToLogicalPath(this MappingArtifactDescriptor descriptor) =>
        $"{descriptor.Scope.Tenant.Value:N}/{descriptor.Scope.Project.Value:N}/{descriptor.Wave.Value:N}/v{descriptor.Version.Value}";
}
