using ArchiveBridge.Application.Performance;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Infrastructure.Performance;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;

namespace ArchiveBridge.Integration.Tests.Performance;

/// <summary>
/// AB-I7-003 §1/§9 — persistência/replay REAL contra SQL Server (nunca em memória) de
/// <see cref="SqlPerformanceBenchmarkResultStore"/>: round-trip fiel, isolamento tenant/projeto, ordenação
/// por recência e comportamento append-only (cada <c>SaveAsync</c> é uma linha nova, nunca uma
/// atualização). O último teste também mede a latência real de <c>SaveAsync</c> desta MESMA store contra o
/// SQL Server do container de CI — a evidência "SQL operation latency" exigida pelo work order, aplicada à
/// store nova (as stores mais profundas do pipeline — plan/execution/reconciliation — exigem uma cadeia de
/// FKs de custódia/plano fora do escopo deste teste; ver performance-baseline-report-i7.md, marcadas
/// <c>NotMeasured</c> nesta Passo).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PerformanceBenchmarkResultStoreTests(SqlServerFixture fixture)
{
    private static readonly BenchmarkDatasetDescriptor Dataset = new("synthetic-store-op", 0, 1, 1);

    [Fact]
    public async Task SavingThenFindingRoundTripsAllFieldsIncludingMeasurements()
    {
        var store = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var scope = SqlServerFixture.NewScope();
        var run = BuildRun(scope, "RoundTripScenario", DateTimeOffset.UtcNow, iterationCount: 3);

        var saved = await store.SaveAsync(run, CancellationToken.None);
        var found = await store.FindRecentAsync(scope, "RoundTripScenario", take: 1, CancellationToken.None);

        var replayed = Assert.Single(found);
        Assert.Equal(saved.Id, replayed.Id);
        Assert.Equal(saved.RecordedAtUtc, replayed.RecordedAtUtc);
        Assert.Equal(run.ScenarioName, replayed.ScenarioName);
        Assert.Equal(run.BuildVersion, replayed.BuildVersion);
        Assert.Equal(run.RuntimeDescription, replayed.RuntimeDescription);
        Assert.Equal(run.HostProfile, replayed.HostProfile);
        Assert.Equal(run.Dataset.Name, replayed.Dataset.Name);
        Assert.Equal(run.Dataset.SizeBytes, replayed.Dataset.SizeBytes);
        Assert.Equal(run.Dataset.ItemCount, replayed.Dataset.ItemCount);
        Assert.Equal(run.Dataset.Seed, replayed.Dataset.Seed);
        Assert.Equal(run.WarmupIterations, replayed.WarmupIterations);
        Assert.Equal(run.Iterations, replayed.Iterations);
        Assert.Equal(run.Measurements.Count, replayed.Measurements.Count);
        for (var i = 0; i < run.Measurements.Count; i++)
        {
            Assert.Equal(run.Measurements[i].IterationIndex, replayed.Measurements[i].IterationIndex);
            Assert.Equal(run.Measurements[i].WallClockMs, replayed.Measurements[i].WallClockMs, precision: 6);
            Assert.Equal(run.Measurements[i].CpuTimeMs, replayed.Measurements[i].CpuTimeMs);
            Assert.Equal(run.Measurements[i].PeakWorkingSetBytes, replayed.Measurements[i].PeakWorkingSetBytes);
            Assert.Equal(run.Measurements[i].BytesProcessed, replayed.Measurements[i].BytesProcessed);
            Assert.Equal(run.Measurements[i].ItemsProcessed, replayed.Measurements[i].ItemsProcessed);
            Assert.Equal(run.Measurements[i].Outcome, replayed.Measurements[i].Outcome);
        }
    }

    [Fact]
    public async Task ARunSavedUnderOneTenantIsInvisibleToADifferentTenantProjectScope()
    {
        var store = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var scopeA = SqlServerFixture.NewScope();
        var scopeB = SqlServerFixture.NewScope();
        var run = BuildRun(scopeA, "IsolationScenario", DateTimeOffset.UtcNow, iterationCount: 1);
        await store.SaveAsync(run, CancellationToken.None);

        var foundForB = await store.FindRecentAsync(scopeB, "IsolationScenario", take: 10, CancellationToken.None);

        Assert.Empty(foundForB);
    }

    [Fact]
    public async Task MultipleSavesForTheSameScenarioAreAppendOnlyOrderedMostRecentFirst()
    {
        var store = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var first = BuildRun(scope, "AppendOnlyScenario", clock.UtcNow, iterationCount: 1);
        await store.SaveAsync(first, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = BuildRun(scope, "AppendOnlyScenario", clock.UtcNow, iterationCount: 1);
        await store.SaveAsync(second, CancellationToken.None);

        var found = await store.FindRecentAsync(scope, "AppendOnlyScenario", take: 10, CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Equal(second.Id, found[0].Id); // mais recente primeiro
        Assert.Equal(first.Id, found[1].Id);
    }

    [Fact]
    public async Task TakeLimitsTheNumberOfResultsReturned()
    {
        var store = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var scope = SqlServerFixture.NewScope();
        for (var i = 0; i < 3; i++)
        {
            await store.SaveAsync(
                BuildRun(scope, "TakeLimitScenario", DateTimeOffset.UtcNow.AddSeconds(i), iterationCount: 1), CancellationToken.None);
        }

        var found = await store.FindRecentAsync(scope, "TakeLimitScenario", take: 2, CancellationToken.None);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task BenchmarkingTheStoresOwnSaveLatencyProducesEvidenceThatCanBePersistedAndReplayed()
    {
        var store = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var harness = new BenchmarkHarness(new SystemClock());
        var scope = SqlServerFixture.NewScope();
        var dataset = new BenchmarkDatasetDescriptor("synthetic-single-measurement-run", 0, 1, 1);

        var run = await harness.RunAsync(
            scope, "PerformanceBenchmarkResultStoreSave", "1.0.0-test", ".NET 10", "ci-sql-container", dataset,
            warmupIterations: 1, iterations: 5,
            workload: async (iteration, ct) =>
            {
                var trivialRun = BuildRun(scope, $"LatencyProbe-{iteration}-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, iterationCount: 1);
                await store.SaveAsync(trivialRun, ct).ConfigureAwait(false);
                return BenchmarkWorkloadOutcome.Success(itemsProcessed: 1);
            },
            CancellationToken.None);

        Assert.Equal(5, run.Measurements.Count);
        Assert.All(run.Measurements, measurement => Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome));

        var saved = await store.SaveAsync(run, CancellationToken.None);
        var replayed = await store.FindRecentAsync(scope, "PerformanceBenchmarkResultStoreSave", take: 1, CancellationToken.None);

        var found = Assert.Single(replayed);
        Assert.Equal(saved.Id, found.Id);
        Assert.Equal(5, found.Measurements.Count);
    }

    private static PerformanceBenchmarkRunRecord BuildRun(
        TenantScope scope, string scenarioName, DateTimeOffset recordedAtUtc, int iterationCount)
    {
        var measurements = Enumerable.Range(0, iterationCount)
            .Select(index => new BenchmarkMeasurement(
                index, wallClockMs: 12.5 + index, cpuTimeMs: 5.0, peakWorkingSetBytes: 1024 * 1024,
                bytesProcessed: 2048, itemsProcessed: 1, BenchmarkIterationOutcome.Success))
            .ToList();

        return PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(), scope.Tenant, scope.Project, scenarioName, "1.0.0-test", ".NET 10",
            "ci-sql-container", Dataset, warmupIterations: 0, iterationCount, measurements, recordedAtUtc);
    }
}
