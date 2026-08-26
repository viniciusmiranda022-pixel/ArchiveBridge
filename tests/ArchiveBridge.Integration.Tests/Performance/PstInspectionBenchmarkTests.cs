using ArchiveBridge.Application.Performance;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;

namespace ArchiveBridge.Integration.Tests.Performance;

/// <summary>
/// AB-I7-003 §1 — harness reproduzível sobre a engine de inspeção PST JÁ ACEITA
/// (<see cref="HeaderOnlyPstInspectionEngine"/>, ADR do Passo 1). Usa uma custódia EM MEMÓRIA (nunca SQL)
/// para que este cenário rode sem infraestrutura externa — o objetivo é medir o custo do adapter de
/// inspeção, não da store. Cabeçalho PST sintético válido (mesma técnica de
/// <c>Slice4bPstProcessingSupport.ValidUnicodeHeader</c>), nunca conteúdo real.
/// </summary>
public sealed class PstInspectionBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ab_bench_pstinsp_" + Guid.NewGuid().ToString("N"));

    public PstInspectionBenchmarkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4 * 1024, "synthetic-pst-tiny-4KiB")]
    [InlineData(1024 * 1024, "synthetic-pst-small-1MiB")]
    public async Task InspectingASyntheticValidPstHeaderProducesThroughputEvidence(int sizeBytes, string datasetLabel)
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var relativeName = "synthetic-" + Guid.NewGuid().ToString("N") + ".pst";
        var bytes = ValidUnicodeHeader(sizeBytes);
        File.WriteAllBytes(Path.Combine(_root, relativeName), bytes);

        var custodyStore = new InMemoryPstCustodyStore();
        var artifact = await custodyStore.RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relativeName),
            DeterministicHash.ComputeBytes(bytes), bytes.LongLength, CancellationToken.None);

        var options = new PstStorageOptions { RootPath = _root };
        var engine = new HeaderOnlyPstInspectionEngine(custodyStore, options);
        var harness = new BenchmarkHarness(new SystemClock());
        var dataset = new BenchmarkDatasetDescriptor(datasetLabel, sizeBytes, itemCount: 1, seed: 1);

        var run = await harness.RunAsync(
            scope, "PstInspection", "1.0.0-test", ".NET 10", "local-sandbox", dataset,
            warmupIterations: 1, iterations: 3,
            workload: async (_, ct) =>
            {
                var result = await engine.InspectAsync(scope, artifact.Id, ct).ConfigureAwait(false);
                Assert.Equal(PstStructuralDiagnostic.Valid, result.Diagnostic);
                return BenchmarkWorkloadOutcome.Success(bytesProcessed: result.ObservedSizeBytes);
            },
            CancellationToken.None);

        Assert.Equal(3, run.Measurements.Count);
        Assert.All(run.Measurements, measurement =>
        {
            Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome);
            Assert.Equal(sizeBytes, measurement.BytesProcessed);
        });
    }

    /// <summary>Cabeçalho PST Unicode 2013+ (wVer=36) sintético válido — mesma técnica de Slice4bPstProcessingSupport.</summary>
    private static byte[] ValidUnicodeHeader(int totalSize)
    {
        var bytes = new byte[totalSize];
        bytes[0] = 0x21; bytes[1] = 0x42; bytes[2] = 0x44; bytes[3] = 0x4E; // dwMagic "!BDN"
        bytes[8] = 0x53; bytes[9] = 0x4D; // wMagicClient "SM"
        bytes[10] = 36; bytes[11] = 0; // wVer = 36 (little-endian)
        for (var i = 12; i < totalSize; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    /// <summary>
    /// Custódia EM MEMÓRIA usada exclusivamente por este cenário de benchmark — nunca no composition root de
    /// produção. Permite exercitar <see cref="HeaderOnlyPstInspectionEngine"/> sem SQL Server real.
    /// </summary>
    private sealed class InMemoryPstCustodyStore : IPstCustodyStore
    {
        private readonly Dictionary<(TenantId, ProjectId, ArtifactId), MigrationArtifact> _artifacts = [];

        public Task<MigrationArtifact?> FindAsync(TenantScope scope, ArtifactId artifact, CancellationToken cancellationToken) =>
            Task.FromResult(_artifacts.TryGetValue((scope.Tenant, scope.Project, artifact), out var found) ? found : null);

        public Task<MigrationArtifact> RegisterAsync(
            TenantId tenant, ProjectId project, PstRelativePath relativePath, Sha256Hash observedHash,
            long observedSizeBytes, CancellationToken cancellationToken)
        {
            var registered = MigrationArtifact.Register(tenant, project, relativePath, observedHash, observedSizeBytes, DateTimeOffset.UtcNow);
            _artifacts[(tenant, project, registered.Id)] = registered;
            return Task.FromResult(registered);
        }
    }
}
