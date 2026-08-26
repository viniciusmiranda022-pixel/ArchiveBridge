using System.Security.Cryptography;
using ArchiveBridge.Application.Performance;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Time;

namespace ArchiveBridge.Integration.Tests.Performance;

/// <summary>
/// AB-I7-003 §1/§7 — harness reproduzível sobre o caminho de hash/streaming de artefato JÁ IMPLEMENTADO
/// (mesmo padrão de <c>IncrementalHash</c> em streaming usado por
/// <c>LocalSinglePartExecutionWriter.CopySourceToStagingAsync</c>). Não depende de SQL Server — pode rodar
/// localmente sem nenhuma infraestrutura externa. Datasets sintéticos determinísticos (conteúdo derivado da
/// seed, nunca dado real), cobrindo classes pequena/média e a fronteira do buffer de streaming interno
/// (4 MiB — <see cref="HeaderOnlyBufferSize"/>).
/// </summary>
public sealed class HashStreamingBenchmarkTests : IDisposable
{
    // Mesmo tamanho de buffer de streaming usado pelo Infrastructure (HeaderOnlyPstInspectionEngine.StreamBufferSize
    // / PartitionOutputBundleValidator.StreamBufferSize) — testar exatamente na fronteira prova que o
    // cenário mede o MESMO padrão de I/O usado em produção, não uma aproximação menor.
    private const int HeaderOnlyBufferSize = 4 * 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ab_bench_hash_" + Guid.NewGuid().ToString("N"));

    public HashStreamingBenchmarkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4 * 1024, "synthetic-tiny-4KiB")] // classe pequena
    [InlineData(256 * 1024, "synthetic-small-256KiB")] // classe pequena/média
    [InlineData(HeaderOnlyBufferSize, "synthetic-boundary-4MiB")] // fronteira do buffer de streaming interno
    public async Task StreamingHashOfASyntheticFileProducesCorrectThroughputEvidence(int sizeBytes, string datasetLabel)
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var path = Path.Combine(_root, "artifact-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = SyntheticBytes(sizeBytes, seed: 7);
        await File.WriteAllBytesAsync(path, bytes);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var harness = new BenchmarkHarness(new SystemClock());
        var dataset = new BenchmarkDatasetDescriptor(datasetLabel, sizeBytes, itemCount: 1, seed: 7);

        var run = await harness.RunAsync(
            scope, "HashStreaming", "1.0.0-test", ".NET 10", "local-sandbox", dataset,
            warmupIterations: 1, iterations: 3,
            workload: async (_, ct) =>
            {
                var observedHash = await StreamHashAsync(path, ct).ConfigureAwait(false);
                Assert.Equal(expectedHash, observedHash, StringComparer.Ordinal);
                return BenchmarkWorkloadOutcome.Success(bytesProcessed: sizeBytes);
            },
            CancellationToken.None);

        Assert.Equal(3, run.Measurements.Count);
        Assert.All(run.Measurements, measurement =>
        {
            Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome);
            Assert.Equal(sizeBytes, measurement.BytesProcessed);
            Assert.NotNull(measurement.BytesPerSecond);
        });
    }

    /// <summary>MESMO padrão de streaming/hash usado por Infrastructure (buffer fixo + <see cref="IncrementalHash"/>), aplicado a um artefato sintético.</summary>
    private static async Task<string> StreamHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, HeaderOnlyBufferSize, useAsync: true);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HeaderOnlyBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha256.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }

    /// <summary>Conteúdo sintético determinístico (derivado apenas da seed/tamanho) — nunca dado real/PII.</summary>
    private static byte[] SyntheticBytes(int size, int seed)
    {
        var bytes = new byte[size];
        var state = seed;
        for (var i = 0; i < size; i++)
        {
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF;
            bytes[i] = (byte)state;
        }

        return bytes;
    }
}
