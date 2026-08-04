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
/// R5-B3: na REPUBLICAÇÃO, quando o diretório final já existe, a publicação valida o BUNDLE COMPLETO
/// (csv + sha256 + manifesto: existência, hash do CSV, valor do sha256, tenant/projeto/onda/versão/
/// caminho/tamanho/ContentSha256, ausência de arquivos inesperados) — não só o hash do CSV. Qualquer
/// inconsistência falha fechada, sem descartar o staging válido nem sobrescrever. E o
/// <see cref="FileSystemMappingArtifactStore.DiscardAsync"/> só apaga um staging ESTRITAMENTE sob
/// <c>&lt;root&gt;/.staging</c> — um caminho arbitrário é recusado antes de qualquer <c>Directory.Delete</c>.
/// </summary>
public sealed class Slice2BundleValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ab_bundle_" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemMappingArtifactStore _store;
    private readonly TenantScope _scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private readonly WaveId _wave = WaveId.New();

    public Slice2BundleValidationTests()
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

    private async Task PublishAsync(int version, byte[] bytes, Sha256Hash hash)
    {
        var staging = await _store.StageAsync(bytes, hash, CancellationToken.None);
        await _store.PublishAsync(staging, Descriptor(version), CancellationToken.None);
    }

    [Fact]
    public async Task ManifestFromAnotherVersionFailsClosed()
    {
        var (bytes, hash) = Payload("v1-content");
        await PublishAsync(1, bytes, hash);
        // Reescreve o manifesto declarando outra VERSÃO (hash/tamanho corretos, versão trocada).
        var alien = $"{{\"Tenant\":\"{_scope.Tenant.Value}\",\"Project\":\"{_scope.Project.Value}\",\"Wave\":\"{_wave.Value}\",\"Version\":2,\"LogicalPath\":\"x\",\"ContentSha256\":\"{hash.Value}\",\"SizeBytes\":{bytes.Length}}}";
        await File.WriteAllTextAsync(Path.Combine(FinalDir(1), "manifest.json"), alien);
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task MissingSidecarFailsClosed()
    {
        var (bytes, hash) = Payload("content");
        await PublishAsync(1, bytes, hash);
        File.Delete(Path.Combine(FinalDir(1), "mapping.sha256"));
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedFileInBundleFailsClosed()
    {
        var (bytes, hash) = Payload("content");
        await PublishAsync(1, bytes, hash);
        await File.WriteAllTextAsync(Path.Combine(FinalDir(1), "unexpected.txt"), "injeção");
        await Assert.ThrowsAsync<MappingGenerationException>(() => _store.GetAsync(Descriptor(1), CancellationToken.None));
    }

    [Fact]
    public async Task RepublishValidatesFullBundleNotOnlyCsvHash()
    {
        // Publica v1; adultera o MANIFESTO já publicado (csv intacto); a republicação com os MESMOS bytes
        // do CSV deve FALHAR — a validação agora é do bundle completo, não só do hash do CSV.
        var (bytes, hash) = Payload("csv-bytes-idênticos");
        await PublishAsync(1, bytes, hash);
        var alien = $"{{\"Tenant\":\"{_scope.Tenant.Value}\",\"Project\":\"{_scope.Project.Value}\",\"Wave\":\"{Guid.NewGuid()}\",\"Version\":1,\"LogicalPath\":\"x\",\"ContentSha256\":\"{hash.Value}\",\"SizeBytes\":{bytes.Length}}}";
        await File.WriteAllTextAsync(Path.Combine(FinalDir(1), "manifest.json"), alien);

        var staging = await _store.StageAsync(bytes, hash, CancellationToken.None);
        await Assert.ThrowsAsync<MappingGenerationException>(() =>
            _store.PublishAsync(staging, Descriptor(1), CancellationToken.None));

        // O staging válido NÃO foi descartado pela recusa (permanece para reconciliação).
        Assert.True(Directory.Exists(staging.StagingPath));
    }

    [Fact]
    public async Task DiscardOutsideStagingIsRejectedAndNothingDeleted()
    {
        // Caminho FORA de <root>/.staging (mesmo dentro da raiz) — descarte recusado, nada apagado.
        var outsideStaging = Path.Combine(_root, "notstaging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideStaging);
        await File.WriteAllTextAsync(Path.Combine(outsideStaging, "keep.txt"), "não apagar");
        var forged = new MappingArtifactStaging(outsideStaging, 0, new Sha256Hash("0000000000000000000000000000000000000000000000000000000000000000"));

        await Assert.ThrowsAsync<ArgumentException>(() => _store.DiscardAsync(forged, CancellationToken.None));
        Assert.True(Directory.Exists(outsideStaging)); // nunca apagou fora da área autorizada
    }

    [Fact]
    public async Task DiscardArbitraryPathOutsideRootIsRejected()
    {
        var arbitrary = Path.Combine(Path.GetTempPath(), "ab_arbitrary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(arbitrary);
        try
        {
            var forged = new MappingArtifactStaging(arbitrary, 0, new Sha256Hash("0000000000000000000000000000000000000000000000000000000000000000"));
            await Assert.ThrowsAsync<ArgumentException>(() => _store.DiscardAsync(forged, CancellationToken.None));
            Assert.True(Directory.Exists(arbitrary));
        }
        finally
        {
            Directory.Delete(arbitrary, recursive: true);
        }
    }

    [Fact]
    public async Task DiscardStagingRootItselfIsRejected()
    {
        var stagingRoot = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var forged = new MappingArtifactStaging(stagingRoot, 0, new Sha256Hash("0000000000000000000000000000000000000000000000000000000000000000"));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DiscardAsync(forged, CancellationToken.None));
        Assert.True(Directory.Exists(stagingRoot));
    }

    [Fact]
    public async Task DiscardLegitimateStagingSucceeds()
    {
        var (bytes, hash) = Payload("staged");
        var staging = await _store.StageAsync(bytes, hash, CancellationToken.None);
        Assert.True(Directory.Exists(staging.StagingPath));
        await _store.DiscardAsync(staging, CancellationToken.None);
        Assert.False(Directory.Exists(staging.StagingPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
