using ArchiveBridge.Application.Performance;
using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Application.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.Performance;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using ArchiveBridge.Integration.Tests;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests.Performance;

/// <summary>
/// AB-I7-004 blocker 1 — benchmark reproduzível de <see cref="SqlReconciliationCertificateStore"/> contra
/// SQL Server REAL, medindo <see cref="IssueReconciliationCertificateUseCase.ExecuteAsync"/> — o caminho de
/// produção real que exercita <see cref="SqlReconciliationCertificateStore.IssueOrConvergeAsync"/> — sobre
/// uma onda plenamente reconciliada e sintética (mesmo cenário "happy PASS" de
/// <c>ReconciliationCertificateIntegrationTests</c>). Satisfaz também o item 1.3 do work order (cenário de
/// reconciliation/certificate executando a lógica real sobre dataset sintético). Cada iteração usa uma
/// FIXTURE MÍNIMA, DETERMINÍSTICA e VÁLIDA que satisfaz de fato a cadeia inteira de FKs do pipeline
/// (projeto → PST executado → onda aprovada → vínculo → precheck → upload verificado → mapping gerado →
/// import job planejado → relatório de resultado importado → snapshots EXO before/after), construída ANTES
/// de <c>harness.RunAsync</c> para que a medição isole a emissão do certificate, não a preparação da
/// evidência.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ReconciliationCertificateStoreBenchmarkTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();

    private static readonly IAuthenticatedActorAccessor Actor =
        new FixedAuthenticatedActorAccessor("bench-admin@contoso.test", PortalRoles.Administrator);

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private SqlPurviewUploadRequestStore UploadRequests() => new(fixture.Factory, Clock);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private SqlMailboxPrecheckStore Prechecks() => new(fixture.Factory);

    private SqlPurviewMappingCsvStore MappingStore() => new(fixture.Factory, Clock);

    private SqlPurviewImportJobStore Jobs() => new(fixture.Factory);

    private SqlPurviewServiceResultReportStore Reports() => new(fixture.Factory);

    private SqlExoArchiveStatisticsStore Snapshots() => new(fixture.Factory);

    private SqlReconciliationAssessmentStore Assessments() => new(fixture.Factory);

    private SqlReconciliationExceptionDispositionStore Dispositions() => new(fixture.Factory);

    private SqlReconciliationCertificateStore Certificates() => new(fixture.Factory);

    private CreateWavePartitionOutputBindingUseCase BindingUseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    private ResolvePurviewMappingEvidenceUseCase EvidenceResolver() => new(
        Slice2Support.WaveStore(fixture), Bindings(), Slice4bPstProcessingSupport.ExecutionStore(fixture),
        UploadRequests(), UploadAttempts(), Prechecks());

    private GeneratePurviewMappingCsvUseCase GenerateMappingUseCase(FileSystemMappingArtifactStore artifacts) =>
        new(EvidenceResolver(), MappingStore(), artifacts, Clock);

    private PlanPurviewImportJobUseCase PlanUseCase() => new(EvidenceResolver(), MappingStore(), Jobs(), Clock);

    private ImportPurviewServiceResultReportUseCase ImportReportUseCase() =>
        new(EvidenceResolver(), MappingStore(), Jobs(), Reports(), Clock);

    private EvaluatePurviewServiceResultCompletenessUseCase CompletenessUseCase() =>
        new(EvidenceResolver(), MappingStore(), Jobs(), Reports());

    private CaptureExoArchiveStatisticsUseCase CaptureUseCase(IExoArchiveStatisticsAdapter adapter) =>
        new(Slice2Support.WaveStore(fixture), Jobs(), adapter, Snapshots(), CompletenessUseCase(), Clock);

    private EvaluateReconciliationUseCase ReconciliationUseCase() =>
        new(EvidenceResolver(), MappingStore(), Jobs(), Reports(), Snapshots(), Assessments(), Clock);

    private IssueReconciliationCertificateUseCase IssueUseCase() =>
        new(ReconciliationUseCase(), EvidenceResolver(), MappingStore(), Assessments(), Dispositions(), Certificates(), Clock, Actor);

    [Fact]
    public async Task IssuingTheCertificateForAFreshFullyMatchedWaveEachIterationProducesRealSqlLatencyEvidenceThatCanBePersistedAndReplayed()
    {
        const int warmupIterations = 1;
        const int iterations = 2;
        var scenarios = new List<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName)>(
            warmupIterations + iterations);
        for (var i = 0; i < warmupIterations + iterations; i++)
        {
            scenarios.Add(await SeedFullyMatchedAsync($"recon-cert-bench-{i}"));
        }

        var harness = new BenchmarkHarness(new SystemUtcClock());
        var dataset = new BenchmarkDatasetDescriptor("synthetic-reconciliation-certificate-issue", sizeBytes: 4096, itemCount: 1, seed: 1);
        var cursor = 0;
        var scopeUsedForHarness = scenarios[0].Scope;

        var run = await harness.RunAsync(
            scopeUsedForHarness, "ReconciliationCertificateStoreIssueOrConverge", "1.0.0-test", ".NET 10", "ci-sql-container", dataset,
            warmupIterations, iterations,
            workload: async (_, ct) =>
            {
                var (scope, wave, plannedJobName) = scenarios[cursor++];
                var certificate = await IssueUseCase()
                    .ExecuteAsync(new IssueReconciliationCertificateCommand(scope, wave, plannedJobName, CorrelationId.New()), ct)
                    .ConfigureAwait(false);
                return BenchmarkWorkloadOutcome.Success(itemsProcessed: certificate.TotalItemCount);
            },
            CancellationToken.None);

        Assert.Equal(iterations, run.Measurements.Count);
        Assert.All(run.Measurements, measurement => Assert.Equal(BenchmarkIterationOutcome.Success, measurement.Outcome));

        var resultStore = new SqlPerformanceBenchmarkResultStore(fixture.Factory);
        var savedRun = await resultStore.SaveAsync(run, CancellationToken.None);
        var replayed = await resultStore.FindRecentAsync(scopeUsedForHarness, "ReconciliationCertificateStoreIssueOrConverge", take: 1, CancellationToken.None);
        var found = Assert.Single(replayed);
        Assert.Equal(savedRun.Id, found.Id);
        Assert.Equal(iterations, found.Measurements.Count);

        var otherScope = SqlServerFixture.NewScope();
        var invisible = await resultStore.FindRecentAsync(otherScope, "ReconciliationCertificateStoreIssueOrConverge", take: 10, CancellationToken.None);
        Assert.Empty(invisible);
    }

    /// <summary>
    /// Onda com UM PST plenamente reconciliado — evidência 100% completa, zero exceções materiais (mesmo
    /// cenário "happy PASS" de <c>ReconciliationCertificateIntegrationTests.SeedFullyMatchedAsync</c>) —
    /// pronta para <see cref="IssueReconciliationCertificateUseCase.ExecuteAsync"/> emitir a versão 1 do
    /// certificate pela primeira vez (nunca reaproveita a fixture de outra iteração, nunca mede o atalho de
    /// convergência idempotente).
    /// </summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName)>
        SeedFullyMatchedAsync(string label)
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

        var precheckSnapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project, entry.Archive, version: 1,
            exchangeGuid: Guid.NewGuid(), archiveGuid: Guid.NewGuid(), MailboxArchiveStatus.Active, "UserMailbox",
            autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: 10, archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000,
            DateTimeOffset.UtcNow, CorrelationId.New(), DateTimeOffset.UtcNow);
        await Prechecks().AppendAsync(precheckSnapshot, CancellationToken.None);

        var enqueue = await UploadRequests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        var jobs = new ArchiveBridge.Infrastructure.Jobs.SqlJobStore(fixture.Factory, Clock, agingInterval: TimeSpan.FromSeconds(30));
        var claimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(
                scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new ArchiveBridge.Domain.Jobs.WorkerId("bench-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);
        var jobFence = new JobFence(scope, claimed!.JobId, new ArchiveBridge.Domain.Jobs.WorkerId("bench-worker"), claimed.Epoch);

        var now = Clock.UtcNow;
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence);
        var manifest = new PurviewUploadFileManifestItem(execution.Id, remoteName, execution.OutputHash, execution.OutputSizeBytes);
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id), [manifest]);
        var attempt = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, now, now);
        await UploadAttempts().AppendAsync(scope, attempt, jobFence, CancellationToken.None);

        var artifacts = new FileSystemMappingArtifactStore(Path.Combine(fixture.ArtifactRoot, "recon-cert-bench-" + Guid.NewGuid().ToString("N")));
        await GenerateMappingUseCase(artifacts).ExecuteAsync(scope, wave.Id, "bench-operator", CancellationToken.None);
        var jobPlan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "bench-operator", CancellationToken.None);

        var reportBytes = ReportBytes(remoteName.Value, "Succeeded", importedItems: 10, importedBytes: 2048);
        await ImportReportUseCase().ExecuteAsync(scope, wave.Id, jobPlan.PlannedJobName, reportBytes, "bench-operator", CancellationToken.None);

        var archive = entry.Archive.Identity;
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(100, 10_000)))
            .ExecuteBeforeImportAsync(scope, wave.Id, archive, CorrelationId.New(), CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(110, 12_000)))
            .ExecuteAfterImportAsync(scope, wave.Id, archive, jobPlan.PlannedJobName, CorrelationId.New(), CancellationToken.None);

        return (scope, wave.Id, jobPlan.PlannedJobName);
    }

    private static readonly ExoArchiveFolderStatisticObservation[] SingleFolder =
        [new("/Top of Information Store/Inbox", "Inbox", 10, 10, 2048, 2048, null, null)];

    private static ExoArchiveStatisticsObservation Observation(long? itemCount, long? totalSizeBytes) =>
        new(
            MailboxArchiveStatus.Active, Guid.NewGuid(), Guid.NewGuid(), itemCount, totalSizeBytes, TotalDeletedItemSizeBytes: 0,
            LastLogonTimeUtc: DateTimeOffset.UtcNow, RetentionHoldEnabled: false, LitigationHoldEnabled: false,
            AutoExpandingArchiveEnabled: false, Folders: SingleFolder, ObservedAtUtc: DateTimeOffset.UtcNow);

    private static byte[] ReportBytes(string remoteName, string status, long? importedItems, long? importedBytes)
    {
        var sb = new System.Text.StringBuilder("RemotePstName,Status,ImportedItemCount,ImportedSizeBytes,SkippedItemCount,CorruptedItemCount\n");
        sb.Append(remoteName).Append(',').Append(status).Append(',');
        sb.Append(importedItems?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
        sb.Append(importedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(",0,0\n");
        return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    private sealed class FixedAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    private sealed class SystemUtcClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
