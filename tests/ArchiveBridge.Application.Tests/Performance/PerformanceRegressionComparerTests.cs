using ArchiveBridge.Application.Performance;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Tests.Performance;

/// <summary>AB-I7-003 §6 — comparação de regressão é sempre informativa, nunca inventa threshold de aprovação/reprovação.</summary>
public sealed class PerformanceRegressionComparerTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId Project = new(Guid.NewGuid());
    private static readonly BenchmarkDatasetDescriptor Dataset = new("synthetic-small", 1024, 1, 1);

    private static PerformanceBenchmarkRunRecord BuildRun(string scenario, params double[] wallClockMsPerIteration)
    {
        var measurements = wallClockMsPerIteration
            .Select((ms, index) => new BenchmarkMeasurement(index, ms, null, null, bytesProcessed: 1000, null, BenchmarkIterationOutcome.Success))
            .ToList();

        return PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, scenario, "1.0.0-test", ".NET 10", "unit-test",
            Dataset, warmupIterations: 0, wallClockMsPerIteration.Length, measurements, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ReportAlwaysCarriesTheInformativeOnlyNotice()
    {
        var baseline = BuildRun("Scenario", 100, 100);
        var current = BuildRun("Scenario", 100, 100);

        var report = PerformanceRegressionComparer.Compare(baseline, current);

        Assert.Equal(PerformanceRegressionComparer.InformativeOnlyNotice, report.Notice);
    }

    [Fact]
    public void MeanWallClockDeltaIsComputedCorrectly()
    {
        var baseline = BuildRun("Scenario", 100, 100); // média 100
        var current = BuildRun("Scenario", 150, 150); // média 150 ⇒ +50%

        var report = PerformanceRegressionComparer.Compare(baseline, current);

        var delta = Assert.Single(report.MetricDeltas, metric => metric.MetricName == "MeanWallClockMs");
        Assert.Equal(100, delta.BaselineValue, precision: 3);
        Assert.Equal(150, delta.CurrentValue, precision: 3);
        Assert.Equal(50, delta.AbsoluteDelta, precision: 3);
        Assert.Equal(50, delta.PercentDelta, precision: 3);
    }

    [Fact]
    public void DifferentScenarioNamesThrow()
    {
        var baseline = BuildRun("ScenarioA", 100);
        var current = BuildRun("ScenarioB", 100);

        Assert.Throws<ArgumentException>(() => PerformanceRegressionComparer.Compare(baseline, current));
    }

    [Fact]
    public void ThroughputDeltaIsOmittedRatherThanZeroedWhenNotApplicable()
    {
        // Nenhuma medição tem bytesProcessed ⇒ BytesPerSecond é sempre null ⇒ a métrica não aparece no
        // relatório (nunca preenchida com zero, que pareceria uma regressão de 100% inventada).
        var measurementsWithoutBytes = new[]
        {
            new BenchmarkMeasurement(0, 100, null, null, bytesProcessed: null, null, BenchmarkIterationOutcome.Success),
        };
        var baseline = PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "Scenario", "1.0.0-test", ".NET 10", "unit-test",
            Dataset, 0, 1, measurementsWithoutBytes, DateTimeOffset.UtcNow);
        var current = PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "Scenario", "1.0.0-test", ".NET 10", "unit-test",
            Dataset, 0, 1, measurementsWithoutBytes, DateTimeOffset.UtcNow);

        var report = PerformanceRegressionComparer.Compare(baseline, current);

        Assert.DoesNotContain(report.MetricDeltas, metric => metric.MetricName == "MeanBytesPerSecond");
    }

    [Fact]
    public void ErrorRateDeltaReflectsChangedFailureRatio()
    {
        var baselineMeasurements = new[]
        {
            new BenchmarkMeasurement(0, 10, null, null, null, null, BenchmarkIterationOutcome.Success),
            new BenchmarkMeasurement(1, 10, null, null, null, null, BenchmarkIterationOutcome.Success),
        };
        var currentMeasurements = new[]
        {
            new BenchmarkMeasurement(0, 10, null, null, null, null, BenchmarkIterationOutcome.Success),
            new BenchmarkMeasurement(1, 10, null, null, null, null, BenchmarkIterationOutcome.Error),
        };
        var baseline = PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "Scenario", "1.0.0-test", ".NET 10", "unit-test",
            Dataset, 0, 2, baselineMeasurements, DateTimeOffset.UtcNow);
        var current = PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), Tenant, Project, "Scenario", "1.0.0-test", ".NET 10", "unit-test",
            Dataset, 0, 2, currentMeasurements, DateTimeOffset.UtcNow);

        var report = PerformanceRegressionComparer.Compare(baseline, current);

        var delta = Assert.Single(report.MetricDeltas, metric => metric.MetricName == "ErrorRatePercent");
        Assert.Equal(0, delta.BaselineValue, precision: 3);
        Assert.Equal(50, delta.CurrentValue, precision: 3);
    }

    [Fact]
    public void ComparingTwoIdenticalRunsIsDeterministic()
    {
        var baseline = BuildRun("Scenario", 100, 110, 90);
        var current = BuildRun("Scenario", 100, 110, 90);

        var report1 = PerformanceRegressionComparer.Compare(baseline, current);
        var report2 = PerformanceRegressionComparer.Compare(baseline, current);

        Assert.Equal(report1.MetricDeltas.Select(metric => metric.MetricName), report2.MetricDeltas.Select(metric => metric.MetricName));
        Assert.All(report1.MetricDeltas, metric => Assert.Equal(0, metric.AbsoluteDelta, precision: 6));
    }
}
