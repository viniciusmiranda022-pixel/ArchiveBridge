using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §9 — determinismo/completude dos resultados: exatamente uma medição por iteração, sem lacuna nem duplicata.</summary>
public sealed class PerformanceBenchmarkRunRecordTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly BenchmarkDatasetDescriptor Dataset = new("synthetic-small", 1024, 1, 1);

    private static BenchmarkMeasurement Measurement(int index, BenchmarkIterationOutcome outcome = BenchmarkIterationOutcome.Success) =>
        new(index, wallClockMs: 10, cpuTimeMs: 5, peakWorkingSetBytes: 1024, bytesProcessed: 512, itemsProcessed: 1, outcome);

    [Fact]
    public void CompleteWithMatchingMeasurementsSucceedsAndOrdersByIterationIndex()
    {
        var measurements = new[] { Measurement(2), Measurement(0), Measurement(1) };

        var record = PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "HashStreaming", "1.0.0-test", ".NET 10", "ci-shared",
            Dataset, warmupIterations: 1, iterations: 3, measurements, DateTimeOffset.UtcNow);

        Assert.Equal([0, 1, 2], record.Measurements.Select(measurement => measurement.IterationIndex));
    }

    [Fact]
    public void MeasurementCountDivergingFromIterationsThrows()
    {
        var measurements = new[] { Measurement(0), Measurement(1) };

        Assert.Throws<ArgumentException>(() => PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "HashStreaming", "1.0.0-test", ".NET 10", "ci-shared",
            Dataset, warmupIterations: 0, iterations: 3, measurements, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DuplicateIterationIndexThrows()
    {
        var measurements = new[] { Measurement(0), Measurement(0) };

        Assert.Throws<ArgumentException>(() => PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "HashStreaming", "1.0.0-test", ".NET 10", "ci-shared",
            Dataset, warmupIterations: 0, iterations: 2, measurements, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MissingIterationIndexThrows()
    {
        var measurements = new[] { Measurement(0), Measurement(2) };

        Assert.Throws<ArgumentException>(() => PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "HashStreaming", "1.0.0-test", ".NET 10", "ci-shared",
            Dataset, warmupIterations: 0, iterations: 3, measurements, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EmptyTenantThrows()
    {
        var measurements = new[] { Measurement(0) };

        Assert.Throws<ArgumentException>(() => PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), new TenantId(Guid.Empty), Project, "HashStreaming", "1.0.0-test", ".NET 10",
            "ci-shared", Dataset, warmupIterations: 0, iterations: 1, measurements, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RehydrateOfACorruptedRowThrowsIntegrityViolationRatherThanArgumentException()
    {
        var measurements = new[] { Measurement(0), Measurement(0) };

        Assert.Throws<PerformanceBenchmarkRunIntegrityViolationException>(() => PerformanceBenchmarkRunRecord.Rehydrate(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "HashStreaming", "1.0.0-test", ".NET 10", "ci-shared",
            Dataset, warmupIterations: 0, iterations: 2, measurements, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AnIterationThatErroredIsStillRecordedNeverSilentlyDropped()
    {
        var measurements = new[] { Measurement(0, BenchmarkIterationOutcome.Error), Measurement(1) };

        var record = PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "HashStreaming", "1.0.0-test", ".NET 10", "ci-shared",
            Dataset, warmupIterations: 0, iterations: 2, measurements, DateTimeOffset.UtcNow);

        Assert.Equal(BenchmarkIterationOutcome.Error, record.Measurements[0].Outcome);
        Assert.Equal(2, record.Measurements.Count);
    }
}
