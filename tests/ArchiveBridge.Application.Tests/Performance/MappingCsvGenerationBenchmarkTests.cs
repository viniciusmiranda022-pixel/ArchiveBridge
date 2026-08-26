using ArchiveBridge.Application.Performance;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Tests.Performance;

/// <summary>
/// AB-I7-003 §1 — cenário de benchmark do harness sobre o gerador PURO de mapping CSV
/// (<see cref="PurviewMappingCsvGenerator"/>): não depende de SQL/filesystem, dataset sintético, mede
/// throughput em linhas/s e bytes/s do CSV serializado.
/// </summary>
public sealed class MappingCsvGenerationBenchmarkTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly TargetRootFolder RootFolder = TargetRootFolder.ForWave("bench", "wave1");

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)] // MappingSchema.MaxDataRows — classe de boundary.
    public async Task GeneratingASyntheticMappingProducesThroughputEvidenceForEveryRowCount(int rowCount)
    {
        var rows = BuildSyntheticRows(rowCount);
        var harness = new BenchmarkHarness(new SystemUtcClock());
        var dataset = new BenchmarkDatasetDescriptor($"synthetic-mapping-{rowCount}-rows", sizeBytes: 0, itemCount: rowCount, seed: 1);

        var run = await harness.RunAsync(
            Scope, "MappingCsvGeneration", "1.0.0-test", ".NET 10", "unit-test", dataset,
            warmupIterations: 1, iterations: 3,
            workload: (_, _) =>
            {
                var result = PurviewMappingCsvGenerator.Generate(
                    WaveId.New(), Scope.Project, RootFolder, rows, DeterministicHash.Compute(["bench"]),
                    new MappingVersion(1), "bench-harness", DateTimeOffset.UtcNow);
                return Task.FromResult(BenchmarkWorkloadOutcome.Success(
                    bytesProcessed: result.Document.Bytes.LongLength, itemsProcessed: result.Document.RowCount));
            },
            CancellationToken.None);

        Assert.Equal(3, run.Measurements.Count);
        Assert.All(run.Measurements, measurement =>
        {
            Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome);
            Assert.Equal(rowCount, measurement.ItemsProcessed);
            Assert.NotNull(measurement.BytesProcessed);
            Assert.True(measurement.BytesProcessed > 0);
        });
    }

    [Fact]
    public async Task ResultsNeverCarryRealMailboxOrFilePathTextOnlyAggregatedCounts()
    {
        var rows = BuildSyntheticRows(5);
        var harness = new BenchmarkHarness(new SystemUtcClock());
        var dataset = new BenchmarkDatasetDescriptor("synthetic-mapping-5-rows", sizeBytes: 0, itemCount: 5, seed: 1);

        var run = await harness.RunAsync(
            Scope, "MappingCsvGeneration", "1.0.0-test", ".NET 10", "unit-test", dataset,
            warmupIterations: 0, iterations: 1,
            workload: (_, _) =>
            {
                var result = PurviewMappingCsvGenerator.Generate(
                    WaveId.New(), Scope.Project, RootFolder, rows, DeterministicHash.Compute(["bench"]),
                    new MappingVersion(1), "bench-harness", DateTimeOffset.UtcNow);
                return Task.FromResult(BenchmarkWorkloadOutcome.Success(bytesProcessed: result.Document.Bytes.LongLength, itemsProcessed: result.Document.RowCount));
            },
            CancellationToken.None);

        // Sanitizado por construção: BenchmarkMeasurement só expõe campos numéricos/enum — nenhum conteúdo
        // do CSV (mailbox/nome de arquivo sintéticos) sobrevive além da contagem agregada.
        var measurement = Assert.Single(run.Measurements);
        Assert.Equal(5, measurement.ItemsProcessed);
        Assert.True(measurement.BytesProcessed > 0);
    }

    private static List<PurviewMappingRow> BuildSyntheticRows(int count) =>
        Enumerable.Range(1, count)
            .Select(index => PurviewMappingRow.Create(
                filePath: $"wave1/synthetic{index:D6}",
                name: $"p_synthetic{index:D6}_part001.pst",
                mailbox: $"synthetic-mailbox-{index:D6}.test",
                isArchive: true,
                targetRootFolder: RootFolder))
            .ToList();

    private sealed class SystemUtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
