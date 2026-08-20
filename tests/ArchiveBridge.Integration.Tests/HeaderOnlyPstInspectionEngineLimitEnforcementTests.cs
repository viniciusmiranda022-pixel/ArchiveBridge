using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-4B-003 — prova que <see cref="PstStorageOptions.MaxSizeBytes"/> é reforçado sobre o stream
/// EFETIVAMENTE lido por <see cref="HeaderOnlyPstInspectionEngine"/>, não apenas sobre o
/// <see cref="FileInfo.Length"/> consultado antes da abertura. Não usa SQL Server (sem
/// <c>[Collection(SqlServerCollectionDefinition.Name)]</c>): a engine é exercitada isoladamente com um
/// duplo de <see cref="IPstCustodyStore"/> e o seam interno <see cref="IPstArtifactStreamFactory"/> — o
/// mesmo padrão de "portas sem Infrastructure" usado em <c>InspectPstArtifactUseCaseTests</c>, adaptado
/// aqui só para a factory de stream, que É Infrastructure e não tem outro ponto de injeção.
/// </summary>
public sealed class HeaderOnlyPstInspectionEngineLimitEnforcementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ab_pst_limit_" + Guid.NewGuid().ToString("N"));

    public HeaderOnlyPstInspectionEngineLimitEnforcementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Simula a janela TOCTOU stat→open (AB-4B-003): o arquivo real em disco é pequeno (passa o fast-fail de
    /// <see cref="FileInfo.Length"/> contra um limite minúsculo), mas o stream EFETIVAMENTE aberto pela
    /// engine — via o seam injetado — produz muito mais bytes do que o limite permite, e nem sequer suporta
    /// <c>Length</c> (simulando metadata prévia ausente/enganosa). Antes da correção, a engine confiava
    /// apenas no <see cref="FileInfo.Length"/> pré-abertura e lia o stream inteiro sem checar o total
    /// acumulado — este teste falha (não lança) na implementação antiga e passa após a correção.
    /// </summary>
    [Fact]
    public async Task LimitIsEnforcedOnTheActuallyReadStreamNotOnlyOnThePreOpenFileInfoLength()
    {
        const long maxSizeBytes = 100;
        const long fabricatedStreamBytes = 20 * 1024 * 1024; // 20 MiB — bem acima do limite e do buffer de 1 chunk.

        var relative = "grows-after-stat.pst";
        var onDiskBytes = new byte[] { 0x21, 0x42, 0x44, 0x4E, 0, 0, 0, 0, 0x53, 0x4D, 36, 0 }; // cabeçalho válido, ínfimo.
        File.WriteAllBytes(Path.Combine(_root, relative), onDiskBytes);
        Assert.True(onDiskBytes.Length <= maxSizeBytes); // pré-condição: o FileInfo.Length real passa no fast-fail.

        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var artifact = ArtifactId.New();
        var custody = MigrationArtifact.Register(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(onDiskBytes), onDiskBytes.Length, DateTimeOffset.UtcNow);
        var custodyStore = new FixedPstCustodyStore(custody);
        var options = new PstStorageOptions { RootPath = _root, MaxSizeBytes = maxSizeBytes };
        var fakeStream = new UnboundedNonSeekableStream(fabricatedStreamBytes);
        var streamFactory = new FixedStreamFactory(fakeStream);
        var engine = new HeaderOnlyPstInspectionEngine(custodyStore, options, streamFactory);

        var exception = await Assert.ThrowsAsync<PstInspectionLimitExceededException>(() =>
            engine.InspectAsync(scope, artifact, CancellationToken.None));

        Assert.Equal("MAX_SIZE_EXCEEDED", exception.ReasonCode);

        // Autoridade final = abortar assim que o total lido exceder o limite, NÃO depois de esgotar o
        // stream inteiro — prova que o enforcement é incremental sobre o stream real, não um "ler tudo e
        // decidir no fim" (que ainda seria fail-closed, mas não é o que o request pediu nem o que protege
        // contra um artefato hostil de tamanho efetivamente ilimitado).
        Assert.True(
            fakeStream.TotalBytesProduced < fabricatedStreamBytes,
            "a engine deveria abortar a leitura antes de consumir o stream inteiro.");
    }

    /// <summary>Mesmo cenário, mas o stream MENTE sobre <c>Length</c> (reporta um valor dentro do limite) — prova que a revalidação de <c>stream.Length</c> não é, sozinha, suficiente; o loop incremental é quem realmente fecha a fronteira.</summary>
    [Fact]
    public async Task LimitIsEnforcedEvenWhenTheStreamsReportedLengthIsMisleading()
    {
        const long maxSizeBytes = 100;
        const long fabricatedStreamBytes = 20 * 1024 * 1024;

        var relative = "lying-length.pst";
        var onDiskBytes = new byte[] { 0x21, 0x42, 0x44, 0x4E, 0, 0, 0, 0, 0x53, 0x4D, 36, 0 };
        File.WriteAllBytes(Path.Combine(_root, relative), onDiskBytes);

        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var artifact = ArtifactId.New();
        var custody = MigrationArtifact.Register(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(onDiskBytes), onDiskBytes.Length, DateTimeOffset.UtcNow);
        var custodyStore = new FixedPstCustodyStore(custody);
        var options = new PstStorageOptions { RootPath = _root, MaxSizeBytes = maxSizeBytes };
        var fakeStream = new SeekableButLyingStream(fabricatedStreamBytes, reportedLength: 10);
        var streamFactory = new FixedStreamFactory(fakeStream);
        var engine = new HeaderOnlyPstInspectionEngine(custodyStore, options, streamFactory);

        var exception = await Assert.ThrowsAsync<PstInspectionLimitExceededException>(() =>
            engine.InspectAsync(scope, artifact, CancellationToken.None));

        Assert.Equal("MAX_SIZE_EXCEEDED", exception.ReasonCode);
        Assert.True(fakeStream.TotalBytesProduced < fabricatedStreamBytes);
    }

    // ---- Duplos de teste ----

    private sealed class FixedPstCustodyStore(MigrationArtifact record) : IPstCustodyStore
    {
        public Task<MigrationArtifact?> FindAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken) =>
            Task.FromResult<MigrationArtifact?>(record);

        public Task<MigrationArtifact> RegisterAsync(
            TenantId tenant, ProjectId project, PstRelativePath relativePath, Sha256Hash observedHash,
            long observedSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Não usado por este teste.");
    }

    private sealed class FixedStreamFactory(Stream stream) : IPstArtifactStreamFactory
    {
        public Stream OpenRead(string absolutePath) => stream;
    }

    /// <summary>Stream sintético que produz <paramref name="totalBytesToProduce"/> bytes e NÃO suporta <c>Length</c> (como um stream de rede) — força a engine a depender só do loop incremental.</summary>
    private class UnboundedNonSeekableStream(long totalBytesToProduce) : Stream
    {
        private long _produced;

        public long TotalBytesProduced => _produced;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> buffer)
        {
            var remaining = totalBytesToProduce - _produced;
            if (remaining <= 0)
            {
                return 0;
            }

            var toWrite = (int)Math.Min(buffer.Length, remaining);
            buffer[..toWrite].Clear();
            _produced += toWrite;
            return toWrite;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Como <see cref="UnboundedNonSeekableStream"/>, mas suporta <c>Length</c> e MENTE — reporta um valor pequeno mesmo produzindo muito mais bytes no <c>Read</c>.</summary>
    private sealed class SeekableButLyingStream(long totalBytesToProduce, long reportedLength) : UnboundedNonSeekableStream(totalBytesToProduce)
    {
        public override bool CanSeek => true;

        public override long Length => reportedLength;
    }
}
