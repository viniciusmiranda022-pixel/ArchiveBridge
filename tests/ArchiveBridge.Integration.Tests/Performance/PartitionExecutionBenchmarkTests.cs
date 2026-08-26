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
/// AB-I7-003 §1/§3 — harness reproduzível sobre o único caminho de partition execution JÁ ACEITO
/// (<see cref="LocalSinglePartExecutionWriter"/>, <c>SinglePartWithinTarget</c>). Não depende de SQL Server
/// — o writer é puramente filesystem. Cada iteração usa um plano/parte NOVO (tenant/projeto/plano
/// aleatórios) para que o caminho de saída determinístico nunca coincida entre iterações — nunca mede o
/// atalho de réplay idempotente por engano, sempre a cópia byte-for-byte real.
/// </summary>
public sealed class PartitionExecutionBenchmarkTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "ab_bench_partexec_src_" + Guid.NewGuid().ToString("N"));
    private readonly string _outputRoot = Path.Combine(Path.GetTempPath(), "ab_bench_partexec_out_" + Guid.NewGuid().ToString("N"));

    public PartitionExecutionBenchmarkTests()
    {
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_outputRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(4 * 1024, "synthetic-part-tiny-4KiB")]
    [InlineData(256 * 1024, "synthetic-part-small-256KiB")]
    public async Task ExecutingASinglePartCopyProducesThroughputEvidenceWithoutHittingTheIdempotentReplayPath(int sizeBytes, string datasetLabel)
    {
        var relative = "artifact-" + Guid.NewGuid().ToString("N") + ".pst";
        var bytes = ValidHeaderBytes(sizeBytes);
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);

        var writer = new LocalSinglePartExecutionWriter(
            new PstStorageOptions { RootPath = _sourceRoot },
            new PartitionExecutionOutputOptions { RootPath = _outputRoot, MinFreeSpaceMarginBytes = 0, Timeout = TimeSpan.FromSeconds(30) },
            new SystemClock());

        var harness = new BenchmarkHarness(new SystemClock());
        var dataset = new BenchmarkDatasetDescriptor(datasetLabel, sizeBytes, itemCount: 1, seed: 1);

        var run = await harness.RunAsync(
            new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid())), "PartitionExecution",
            "1.0.0-test", ".NET 10", "local-sandbox", dataset,
            warmupIterations: 1, iterations: 3,
            workload: async (_, ct) =>
            {
                var (scope, custody, plan, part) = BuildFreshPlanFor(relative, bytes);
                var artifact = await writer.ExecuteAsync(
                    scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow), ct)
                    .ConfigureAwait(false);
                return BenchmarkWorkloadOutcome.Success(bytesProcessed: artifact.OutputSizeBytes);
            },
            CancellationToken.None);

        Assert.Equal(3, run.Measurements.Count);
        Assert.All(run.Measurements, measurement =>
        {
            Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome);
            Assert.Equal(sizeBytes, measurement.BytesProcessed);
        });
    }

    private static byte[] ValidHeaderBytes(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0x21; bytes[1] = 0x42; bytes[2] = 0x44; bytes[3] = 0x4E;
        return bytes;
    }

    /// <summary>
    /// Um NOVO tenant/projeto/plano a cada chamada — o caminho de saída determinístico
    /// (tenant/projeto/plano/part-key) nunca se repete entre iterações, então o writer sempre materializa
    /// uma cópia nova (nunca o atalho de réplay idempotente do diretório já existente).
    /// </summary>
    private static (TenantScope Scope, MigrationArtifact Custody, PartitionPlan Plan, PartitionPlanPart Part) BuildFreshPlanFor(
        string relative, byte[] bytes)
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var hash = DeterministicHash.ComputeBytes(bytes);
        var custody = MigrationArtifact.Register(
            scope.Tenant, scope.Project, new PstRelativePath(relative), hash, bytes.Length, DateTimeOffset.UtcNow);
        var planner = new PartitionPlannerIdentity("BenchmarkPlanner", "1.0");
        var policy = PartitionPolicy.Create(bytes.Length * 2L, bytes.Length * 4L);
        var source = new PartitionPlanSource(custody.Id, InspectionId.New(), hash, bytes.Length, "Engine", "1.0");
        var planHash = PartitionPlanIdentity.ComputePlanHash(scope.Tenant, scope.Project, source, policy, planner);
        var part = new PartitionPlanPart(
            PartitionPlanPartId.New(), 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), bytes.Length, coversEntireSource: true);
        var plan = PartitionPlan.Create(
            PartitionPlanId.New(), scope.Tenant, scope.Project, source, policy, planner, planHash,
            PartitionPlanReason.SinglePartWithinTarget, [part], CorrelationId.New(), DateTimeOffset.UtcNow);
        return (scope, custody, plan, part);
    }
}
