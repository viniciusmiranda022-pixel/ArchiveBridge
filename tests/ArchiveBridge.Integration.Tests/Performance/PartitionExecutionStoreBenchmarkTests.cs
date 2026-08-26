using ArchiveBridge.Application.Performance;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Performance;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests.Performance;

/// <summary>
/// AB-I7-004 blocker 1 — benchmark reproduzível de <see cref="SqlPartitionExecutionStore"/> contra SQL
/// Server REAL (a store de execução/custódia mais crítica do pipeline apontada pelo Engineering Reviewer),
/// medindo especificamente a latência de <see cref="SqlPartitionExecutionStore.SaveAsync"/> — não o writer
/// de filesystem, que já é coberto por <see cref="PartitionExecutionBenchmarkTests"/>. Cada iteração usa
/// uma FIXTURE MÍNIMA, DETERMINÍSTICA e VÁLIDA que satisfaz de fato as FKs reais (custódia → inspeção →
/// plano, todas persistidas via os mesmos casos de uso da Slice 4B): nenhuma constraint é contornada, nenhum
/// SQL inválido é inserido só para o benchmark. As fixtures são construídas ANTES de <c>harness.RunAsync</c>
/// (fora da região medida) para que a medição isole a chamada real ao store, não a preparação da fixture.
/// <see cref="PartitionExecutionRecord.Complete"/> é chamado com <c>output == source</c> (o mesmo invariante
/// estrutural que <c>SinglePartWithinTarget</c> exige em produção) sem materializar bytes em disco — a store
/// nunca lê o filesystem, só persiste o manifesto estrutural.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PartitionExecutionStoreBenchmarkTests(SqlServerFixture fixture) : IDisposable
{
    private readonly List<string> _writtenFiles = [];

    public void Dispose()
    {
        foreach (var path in _writtenFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SavingAFreshExecutionForEachIterationProducesRealSqlLatencyEvidenceThatCanBePersistedAndReplayed()
    {
        const int warmupIterations = 1;
        const int iterations = 3;
        var fixtures = new List<(TenantScope Scope, PartitionExecutionRecord Record)>(warmupIterations + iterations);
        for (var i = 0; i < warmupIterations + iterations; i++)
        {
            fixtures.Add(await BuildFreshExecutionFixtureAsync($"partexec-store-bench-{i}.pst"));
        }

        var executionStore = Slice4bPstProcessingSupport.ExecutionStore(fixture);
        var harness = new BenchmarkHarness(new SystemUtcClock());
        var dataset = new BenchmarkDatasetDescriptor("synthetic-partition-execution-save", sizeBytes: 4096, itemCount: 1, seed: 1);
        var cursor = 0;
        var scopeUsedForHarness = fixtures[0].Scope;

        var run = await harness.RunAsync(
            scopeUsedForHarness, "PartitionExecutionStoreSave", "1.0.0-test", ".NET 10", "ci-sql-container", dataset,
            warmupIterations, iterations,
            workload: async (_, ct) =>
            {
                var (_, record) = fixtures[cursor++];
                var saved = await executionStore.SaveAsync(record, ct).ConfigureAwait(false);
                return BenchmarkWorkloadOutcome.Success(bytesProcessed: saved.OutputSizeBytes, itemsProcessed: 1);
            },
            CancellationToken.None);

        Assert.Equal(iterations, run.Measurements.Count);
        Assert.All(run.Measurements, measurement =>
        {
            Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome);
            Assert.Equal(4096, measurement.BytesProcessed);
        });

        // Persistência/replay da evidência (AB-I7-003/004 §1.4): a própria store nova de resultados de
        // benchmark, exatamente como PerformanceBenchmarkResultStoreTests já faz para os outros cenários.
        var resultStore = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var savedRun = await resultStore.SaveAsync(run, CancellationToken.None);
        var replayed = await resultStore.FindRecentAsync(scopeUsedForHarness, "PartitionExecutionStoreSave", take: 1, CancellationToken.None);
        var found = Assert.Single(replayed);
        Assert.Equal(savedRun.Id, found.Id);
        Assert.Equal(iterations, found.Measurements.Count);

        // Isolamento tenant/project (AB-I7-003/004 §1.4): um escopo diferente nunca enxerga esta evidência.
        var otherScope = SqlServerFixture.NewScope();
        var invisible = await resultStore.FindRecentAsync(otherScope, "PartitionExecutionStoreSave", take: 10, CancellationToken.None);
        Assert.Empty(invisible);
    }

    /// <summary>
    /// Registra custódia, inspeciona e planeja (SinglePartWithinTarget) um PST sintético MÍNIMO — a cadeia
    /// real de FKs exigida por <c>pst_partition_executions</c> (plano → parte → artefato, todos já
    /// persistidos em SQL) — e devolve um <see cref="PartitionExecutionRecord"/> pronto para
    /// <c>SaveAsync</c>, com <c>output == source</c> (mesmo invariante de <c>SinglePartWithinTarget</c>),
    /// SEM materializar nenhum byte de output em disco: só a store é medida, não o writer de filesystem.
    /// </summary>
    private async Task<(TenantScope Scope, PartitionExecutionRecord Record)> BuildFreshExecutionFixtureAsync(string name)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader(totalSize: 4096);
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, $"{Guid.NewGuid():N}-{name}", bytes);
        _writtenFiles.Add(Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), relative));

        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative), DeterministicHash.ComputeBytes(bytes), bytes.Length,
            CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var part = plan.Parts[0];
        var now = DateTimeOffset.UtcNow;

        var record = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, artifact.Id, plan.Id, part.Id,
            plan.PlanHash, part.Sequence, part.PartKey, plan.Source.SourceHash, bytes.Length,
            plan.Source.SourceHash, bytes.Length, // output == source (invariante estrutural de SinglePartWithinTarget)
            new PartitionExecutorIdentity("ArchiveBridge.BenchmarkHarness", "1.0.0"), CorrelationId.New(), now, now);

        return (scope, record);
    }

    private sealed class SystemUtcClock : ArchiveBridge.Contracts.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
