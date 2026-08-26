using ArchiveBridge.Application.Performance;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.Tests.Performance;

/// <summary>AB-I7-003 §1/§9 — o harness é reproduzível, nunca omite uma iteração e nunca deixa o aquecimento vazar para a evidência.</summary>
public sealed class BenchmarkHarnessTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly BenchmarkDatasetDescriptor Dataset = new("synthetic-small", 1024, 1, 1);

    [Fact]
    public async Task RunAsyncProducesExactlyOneMeasurementPerIterationExcludingWarmup()
    {
        var harness = new BenchmarkHarness(new StubClock(DateTimeOffset.UtcNow));
        var calls = 0;

        var run = await harness.RunAsync(
            Scope, "TestScenario", "1.0.0-test", ".NET 10", "unit-test", Dataset,
            warmupIterations: 2, iterations: 5,
            workload: (_, _) =>
            {
                calls++;
                return Task.FromResult(BenchmarkWorkloadOutcome.Success(bytesProcessed: 10, itemsProcessed: 1));
            },
            CancellationToken.None);

        Assert.Equal(7, calls); // 2 warmup + 5 medidas
        Assert.Equal(5, run.Measurements.Count);
        Assert.Equal(2, run.WarmupIterations);
        Assert.Equal(5, run.Iterations);
        Assert.Equal([0, 1, 2, 3, 4], run.Measurements.Select(measurement => measurement.IterationIndex));
    }

    [Fact]
    public async Task AWorkloadThatThrowsIsRecordedAsErrorAndSubsequentIterationsStillRun()
    {
        var harness = new BenchmarkHarness(new StubClock(DateTimeOffset.UtcNow));

        var run = await harness.RunAsync(
            Scope, "TestScenario", "1.0.0-test", ".NET 10", "unit-test", Dataset,
            warmupIterations: 0, iterations: 3,
            workload: (index, _) => index == 1
                ? throw new InvalidOperationException("erro sintético de teste")
                : Task.FromResult(BenchmarkWorkloadOutcome.Success()),
            CancellationToken.None);

        Assert.Equal(3, run.Measurements.Count);
        Assert.Equal(BenchmarkIterationOutcome.Success, run.Measurements[0].Outcome);
        Assert.Equal(BenchmarkIterationOutcome.Error, run.Measurements[1].Outcome);
        Assert.Equal(BenchmarkIterationOutcome.Success, run.Measurements[2].Outcome);
    }

    [Fact]
    public async Task AnErrorDuringWarmupNeverBecomesEvidenceAndNeverStopsTheHarness()
    {
        var harness = new BenchmarkHarness(new StubClock(DateTimeOffset.UtcNow));

        var run = await harness.RunAsync(
            Scope, "TestScenario", "1.0.0-test", ".NET 10", "unit-test", Dataset,
            warmupIterations: 1, iterations: 1,
            workload: (_, _) => throw new InvalidOperationException("erro sintético de aquecimento/medida"),
            CancellationToken.None);

        // O erro ocorre TANTO no aquecimento quanto na única iteração medida — a medida ainda é registrada
        // (como Error), mas nenhuma medição extra aparece por causa do aquecimento.
        Assert.Single(run.Measurements);
        Assert.Equal(BenchmarkIterationOutcome.Error, run.Measurements[0].Outcome);
    }

    [Fact]
    public async Task CancellationRequestedByTheCallerPropagatesRatherThanBeingRecordedAsAMeasurement()
    {
        var harness = new BenchmarkHarness(new StubClock(DateTimeOffset.UtcNow));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.RunAsync(
            Scope, "TestScenario", "1.0.0-test", ".NET 10", "unit-test", Dataset,
            warmupIterations: 0, iterations: 5,
            workload: (index, _) =>
            {
                if (index == 2)
                {
                    cts.Cancel();
                }

                cts.Token.ThrowIfCancellationRequested();
                return Task.FromResult(BenchmarkWorkloadOutcome.Success());
            },
            cts.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task IterationsBelowOneThrows(int iterations)
    {
        var harness = new BenchmarkHarness(new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.RunAsync(
            Scope, "TestScenario", "1.0.0-test", ".NET 10", "unit-test", Dataset,
            warmupIterations: 0, iterations: iterations,
            workload: (_, _) => Task.FromResult(BenchmarkWorkloadOutcome.Success()),
            CancellationToken.None));
    }

    [Fact]
    public async Task NegativeWarmupIterationsThrows()
    {
        var harness = new BenchmarkHarness(new StubClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.RunAsync(
            Scope, "TestScenario", "1.0.0-test", ".NET 10", "unit-test", Dataset,
            warmupIterations: -1, iterations: 1,
            workload: (_, _) => Task.FromResult(BenchmarkWorkloadOutcome.Success()),
            CancellationToken.None));
    }

    [Fact]
    public async Task MetadataIsCapturedExactlyAsProvided()
    {
        var recordedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var harness = new BenchmarkHarness(new StubClock(recordedAt));

        var run = await harness.RunAsync(
            Scope, "HashStreaming", "1.2.3+abc", ".NET 10.0.110 (linux-x64)", "ci-shared", Dataset,
            warmupIterations: 0, iterations: 1,
            workload: (_, _) => Task.FromResult(BenchmarkWorkloadOutcome.Success()),
            CancellationToken.None);

        Assert.Equal("HashStreaming", run.ScenarioName);
        Assert.Equal("1.2.3+abc", run.BuildVersion);
        Assert.Equal(".NET 10.0.110 (linux-x64)", run.RuntimeDescription);
        Assert.Equal("ci-shared", run.HostProfile);
        Assert.Same(Dataset, run.Dataset);
        Assert.Equal(Scope.Tenant, run.Tenant);
        Assert.Equal(Scope.Project, run.Project);
        Assert.Equal(recordedAt, run.RecordedAtUtc);
    }

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
