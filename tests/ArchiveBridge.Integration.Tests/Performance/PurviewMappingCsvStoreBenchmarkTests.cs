using ArchiveBridge.Application.Performance;
using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.Performance;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests.Performance;

/// <summary>
/// AB-I7-004 blocker 1 — benchmark reproduzível de <see cref="SqlPurviewMappingCsvStore"/> contra SQL Server
/// REAL, medindo o round-trip completo das duas transações do protocolo recuperável
/// (<see cref="SqlPurviewMappingCsvStore.ReserveAsync"/> → publicação do artefato fora do SQL →
/// <see cref="SqlPurviewMappingCsvStore.FinalizeAsync"/>) através de <see cref="GeneratePurviewMappingCsvUseCase"/>
/// — o caminho de produção real, não uma chamada isolada e artificial à store. Cada iteração usa uma
/// FIXTURE MÍNIMA, DETERMINÍSTICA e VÁLIDA que satisfaz de fato as FKs reais (projeto → onda aprovada →
/// execução de partição → vínculo → precheck de mailbox → upload verificado), construída ANTES de
/// <c>harness.RunAsync</c> para que a medição isole a chamada ao caso de uso/store, não a preparação da
/// evidência.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PurviewMappingCsvStoreBenchmarkTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private SqlPurviewUploadRequestStore UploadRequests() => new(fixture.Factory, Clock);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private SqlMailboxPrecheckStore Prechecks() => new(fixture.Factory);

    private SqlPurviewMappingCsvStore MappingStore() => new(fixture.Factory, Clock);

    private CreateWavePartitionOutputBindingUseCase BindingUseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    private ResolvePurviewMappingEvidenceUseCase EvidenceResolver() => new(
        Slice2Support.WaveStore(fixture), Bindings(), Slice4bPstProcessingSupport.ExecutionStore(fixture),
        UploadRequests(), UploadAttempts(), Prechecks());

    private GeneratePurviewMappingCsvUseCase GenerateUseCase(FileSystemMappingArtifactStore artifacts) =>
        new(EvidenceResolver(), MappingStore(), artifacts, Clock);

    [Fact]
    public async Task GeneratingTheMappingForAFreshVerifiedWaveEachIterationProducesRealSqlLatencyEvidenceThatCanBePersistedAndReplayed()
    {
        const int warmupIterations = 1;
        const int iterations = 2;
        var scenarios = new List<(TenantScope Scope, WaveId Wave, FileSystemMappingArtifactStore Artifacts)>(warmupIterations + iterations);
        for (var i = 0; i < warmupIterations + iterations; i++)
        {
            scenarios.Add(await SeedVerifiedSingleEntryWaveAsync($"mapping-store-bench-{i}"));
        }

        var harness = new BenchmarkHarness(new SystemUtcClock());
        var dataset = new BenchmarkDatasetDescriptor("synthetic-mapping-csv-store-round-trip", sizeBytes: 4096, itemCount: 1, seed: 1);
        var cursor = 0;
        var scopeUsedForHarness = scenarios[0].Scope;

        var run = await harness.RunAsync(
            scopeUsedForHarness, "PurviewMappingCsvStoreReserveAndFinalize", "1.0.0-test", ".NET 10", "ci-sql-container", dataset,
            warmupIterations, iterations,
            workload: async (_, ct) =>
            {
                var (scope, wave, artifacts) = scenarios[cursor++];
                var outcome = await GenerateUseCase(artifacts).ExecuteAsync(scope, wave, "bench-operator", ct).ConfigureAwait(false);
                return BenchmarkWorkloadOutcome.Success(bytesProcessed: outcome.Document.Bytes.LongLength, itemsProcessed: outcome.Document.RowCount);
            },
            CancellationToken.None);

        Assert.Equal(iterations, run.Measurements.Count);
        Assert.All(run.Measurements, measurement =>
        {
            Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome);
            Assert.Equal(1, measurement.ItemsProcessed);
        });

        var resultStore = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var savedRun = await resultStore.SaveAsync(run, CancellationToken.None);
        var replayed = await resultStore.FindRecentAsync(scopeUsedForHarness, "PurviewMappingCsvStoreReserveAndFinalize", take: 1, CancellationToken.None);
        var found = Assert.Single(replayed);
        Assert.Equal(savedRun.Id, found.Id);
        Assert.Equal(iterations, found.Measurements.Count);

        var otherScope = SqlServerFixture.NewScope();
        var invisible = await resultStore.FindRecentAsync(otherScope, "PurviewMappingCsvStoreReserveAndFinalize", take: 10, CancellationToken.None);
        Assert.Empty(invisible);
    }

    /// <summary>
    /// Constrói o cenário completo e verificado (onda aprovada, 1 entrada, PST executado, vínculo, upload
    /// verificado, precheck Active) — a MESMA cadeia real usada por <c>PurviewMappingCsvIntegrationTests</c>
    /// — pronto para <see cref="GeneratePurviewMappingCsvUseCase.ExecuteAsync"/> gerar/persistir a versão
    /// pela primeira vez (nunca reaproveita a fixture de outra iteração, nunca mede o atalho idempotente).
    /// </summary>
    private async Task<(TenantScope Scope, WaveId Wave, FileSystemMappingArtifactStore Artifacts)> SeedVerifiedSingleEntryWaveAsync(string label)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var name = $"{label}-{Guid.NewGuid():N}.pst";
        var mailbox = $"{label}@contoso.test";
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader(totalSize: 4096);
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative), DeterministicHash.ComputeBytes(bytes), bytes.Length,
            CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var execution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture).ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        var entry = Slice2Support.Entry(name, mailbox, execution.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entry])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        var precheckVersion = 1;
        var precheckSnapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project, entry.Archive, precheckVersion,
            exchangeGuid: Guid.NewGuid(), archiveGuid: Guid.NewGuid(), MailboxArchiveStatus.Active, "UserMailbox",
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: 10, archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000,
            DateTimeOffset.UtcNow, CorrelationId.New(), DateTimeOffset.UtcNow);
        await Prechecks().AppendAsync(precheckSnapshot, CancellationToken.None);

        var enqueue = await UploadRequests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        var jobs = new ArchiveBridge.Infrastructure.Jobs.SqlJobStore(fixture.Factory, Clock, agingInterval: TimeSpan.FromSeconds(30));
        var claimed = await jobs.TryClaimNextAsync(
            new ArchiveBridge.Contracts.Jobs.ClaimRequest(
                scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new ArchiveBridge.Domain.Jobs.WorkerId("bench-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);
        var jobFence = new JobFence(scope, claimed!.JobId, new ArchiveBridge.Domain.Jobs.WorkerId("bench-worker"), claimed.Epoch);

        var now = Clock.UtcNow;
        var manifest = new PurviewUploadFileManifestItem(
            execution.Id, PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence), execution.OutputHash, execution.OutputSizeBytes);
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id), [manifest]);
        var attempt = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, now, now);
        await UploadAttempts().AppendAsync(scope, attempt, jobFence, CancellationToken.None);

        var artifacts = new FileSystemMappingArtifactStore(Path.Combine(fixture.ArtifactRoot, "purview-mapping-bench-" + Guid.NewGuid().ToString("N")));
        return (scope, wave.Id, artifacts);
    }

    private sealed class SystemUtcClock : ArchiveBridge.Contracts.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
