using System.Data;
using System.Text;
using ArchiveBridge.Application.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I6-007 (SQL Server real) — <see cref="EvaluateReconciliationUseCase"/> e
/// <see cref="SqlReconciliationAssessmentStore"/>: resolução server-side do conjunto esperado pela cadeia
/// canônica, correlação com o service result do Purview (Passo 1) e com os snapshots EXO before/after
/// (Passo 2), drift/tampering bloqueando avaliação canônica, convergência idempotente por
/// <see cref="ReconciliationAssessment.SourceFingerprint"/> e STOP-THE-LINE (nenhuma escrita EXO/Graph/
/// Purview/EV, nenhuma conclusão de wave/projeto).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ReconciliationIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private SqlPurviewUploadRequestStore UploadRequests() => new(fixture.Factory, Clock);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private SqlMailboxPrecheckStore Prechecks() => new(fixture.Factory);

    private SqlPurviewMappingCsvStore MappingStore() => new(fixture.Factory, Clock);

    private SqlPurviewImportJobStore Jobs() => new(fixture.Factory);

    private SqlPurviewServiceResultReportStore Reports() => new(fixture.Factory);

    private SqlExoArchiveStatisticsStore Snapshots() => new(fixture.Factory);

    private SqlReconciliationAssessmentStore Assessments() => new(fixture.Factory);

    private FileSystemMappingArtifactStore Artifacts() =>
        new(Path.Combine(fixture.ArtifactRoot, "reconciliation-" + Guid.NewGuid().ToString("N")));

    private CreateWavePartitionOutputBindingUseCase BindingUseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    private ResolvePurviewMappingEvidenceUseCase EvidenceResolver() => new(
        Slice2Support.WaveStore(fixture), Bindings(), Slice4bPstProcessingSupport.ExecutionStore(fixture),
        UploadRequests(), UploadAttempts(), Prechecks());

    private GeneratePurviewMappingCsvUseCase GenerateMappingUseCase() => new(EvidenceResolver(), MappingStore(), Artifacts(), Clock);

    private PlanPurviewImportJobUseCase PlanUseCase() => new(EvidenceResolver(), MappingStore(), Jobs(), Clock);

    private ImportPurviewServiceResultReportUseCase ImportReportUseCase() => new(EvidenceResolver(), MappingStore(), Jobs(), Reports(), Clock);

    private EvaluatePurviewServiceResultCompletenessUseCase CompletenessUseCase() =>
        new(EvidenceResolver(), MappingStore(), Jobs(), Reports());

    private CaptureExoArchiveStatisticsUseCase CaptureUseCase(IExoArchiveStatisticsAdapter adapter) =>
        new(Slice2Support.WaveStore(fixture), Jobs(), adapter, Snapshots(), CompletenessUseCase(), Clock);

    private EvaluateReconciliationUseCase ReconciliationUseCase() =>
        new(EvidenceResolver(), MappingStore(), Jobs(), Reports(), Snapshots(), Assessments(), Clock);

    private async Task<PartitionExecutionRecord> RegisterAndExecuteAsync(TenantScope scope, string name)
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative), DeterministicHash.ComputeBytes(bytes), bytes.Length,
            CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        return await Slice4bPstProcessingSupport.ExecuteUseCase(fixture).ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
    }

    private async Task SeedPrecheckAsync(TenantScope scope, WaveEntry entry, MailboxArchiveStatus status)
    {
        var latest = await Prechecks().GetLatestAsync(scope, entry.Archive.Identity, CancellationToken.None);
        var nextVersion = (latest?.Version ?? 0) + 1;
        var snapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project, entry.Archive, nextVersion,
            exchangeGuid: Guid.NewGuid(), archiveGuid: status == MailboxArchiveStatus.Active ? Guid.NewGuid() : null,
            status, "UserMailbox", autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: 10, archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000,
            DateTimeOffset.UtcNow, CorrelationId.New(), DateTimeOffset.UtcNow);
        await Prechecks().AppendAsync(snapshot, CancellationToken.None);
    }

    private static PurviewUploadFileManifestItem CanonicalManifestItem(PartitionExecutionRecord execution) =>
        new(execution.Id, PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence), execution.OutputHash, execution.OutputSizeBytes);

    private async Task MarkUploadVerifiedAsync(TenantScope scope, MigrationWave wave, IReadOnlyList<PartitionExecutionRecord> executions)
    {
        var enqueue = await UploadRequests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        var jobs = new ArchiveBridge.Infrastructure.Jobs.SqlJobStore(fixture.Factory, Clock, agingInterval: TimeSpan.FromSeconds(30));
        var claimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(
                scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new ArchiveBridge.Domain.Jobs.WorkerId("test-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);
        var fence = new JobFence(scope, claimed!.JobId, new ArchiveBridge.Domain.Jobs.WorkerId("test-worker"), claimed.Epoch);

        var now = Clock.UtcNow;
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id), [.. executions.Select(CanonicalManifestItem)]);
        var record = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, now, now);
        await UploadAttempts().AppendAsync(scope, record, fence, CancellationToken.None);
    }

    /// <summary>
    /// Onda com N entradas resolvidas, vínculos canônicos, upload verificado, mapping publicado e plano de
    /// import job criado — o piso completo para reconciliar (AB-I6-007). Cada tupla é (nome do PST, mailbox).
    /// </summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, List<(WaveEntry Entry, PartitionExecutionRecord Execution)> Entries, PurviewImportJobName PlannedJobName)>
        SeedPlannedWaveAsync(params (string Name, string Mailbox)[] pstEntries)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var entries = new List<(WaveEntry Entry, PartitionExecutionRecord Execution)>();
        foreach (var (name, mailbox) in pstEntries)
        {
            var execution = await RegisterAndExecuteAsync(scope, name);
            var entry = Slice2Support.Entry(name, mailbox, execution.OutputSizeBytes);
            entries.Add((entry, execution));
        }

        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([.. entries.Select(item => item.Entry)])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        foreach (var (entry, execution) in entries)
        {
            await BindingUseCase().ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
                CancellationToken.None);
            await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);
        }

        await MarkUploadVerifiedAsync(scope, wave, [.. entries.Select(item => item.Execution)]);
        await GenerateMappingUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        return (scope, wave, entries, plan.PlannedJobName);
    }

    private static string RemoteNameFor(PartitionExecutionRecord execution) =>
        PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;

    private static byte[] ReportBytes(IReadOnlyList<(string RemoteName, string Status, long? ImportedItems, long? ImportedBytes)> rows)
    {
        var sb = new StringBuilder("RemotePstName,Status,ImportedItemCount,ImportedSizeBytes\n");
        foreach (var (remoteName, status, importedItems, importedBytes) in rows)
        {
            sb.Append(remoteName).Append(',').Append(status).Append(',');
            sb.Append(importedItems?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(importedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    private static readonly ExoArchiveFolderStatisticObservation[] SingleFolder =
        [new("/Top of Information Store/Inbox", "Inbox", 10, 10, 2048, 2048, null, null)];

    private static ExoArchiveStatisticsObservation Observation(long? itemCount, long? totalSizeBytes) =>
        new(
            MailboxArchiveStatus.Active, Guid.NewGuid(), Guid.NewGuid(), itemCount, totalSizeBytes, TotalDeletedItemSizeBytes: 0,
            LastLogonTimeUtc: DateTimeOffset.UtcNow, RetentionHoldEnabled: false, LitigationHoldEnabled: false,
            AutoExpandingArchiveEnabled: false, Folders: SingleFolder, ObservedAtUtc: DateTimeOffset.UtcNow);

    // ---- Happy path ----

    [Fact]
    public async Task EvaluateProducesMatchedWithinEvidenceForACompleteExpectedSetAndConclusiveObservedEvidence()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-happy.pst", "recon-happy@contoso.com"));
        var execution = entries[0].Execution;
        var remoteName = RemoteNameFor(execution);
        var archive = entries[0].Entry.Archive.Identity;

        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(100, 10_000)))
            .ExecuteBeforeImportAsync(scope, wave.Id, archive, CorrelationId.New(), CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(110, 12_000)))
            .ExecuteAfterImportAsync(scope, wave.Id, archive, plannedJobName, CorrelationId.New(), CancellationToken.None);

        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(1, assessment.AssessmentVersion);
        Assert.Equal(1, assessment.PstItemCount);
        Assert.Equal(1, assessment.ArchiveItemCount);

        var pstItems = await Assessments().GetPstItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None);
        Assert.Equal(ReconciliationDisposition.MatchedWithinEvidence, Assert.Single(pstItems).Disposition);

        var archiveItems = await Assessments().GetArchiveItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None);
        var archiveItem = Assert.Single(archiveItems);
        Assert.Equal(ReconciliationDisposition.MatchedWithinEvidence, archiveItem.Disposition);
        Assert.Equal(10, archiveItem.ItemCountDelta);

        var summary = ReconciliationWaveSummary.From(pstItems, archiveItems);
        Assert.Equal(1, summary.PstMatched);
        Assert.Equal(1, summary.ArchiveMatched);

        // STOP-THE-LINE: a onda continua exatamente no estado em que estava — nenhuma conclusão/certificate.
        var reread = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(wave.Status, reread!.Status);
    }

    [Fact]
    public async Task EvaluateMarksAnExpectedPstAbsentFromTheProviderResultAsIncompleteEvidence()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(
            ("recon-missing-a.pst", "recon-missing-a@contoso.com"), ("recon-missing-b.pst", "recon-missing-b@contoso.com"));
        var coveredRemoteName = RemoteNameFor(entries[0].Execution);

        // O relatório cobre SOMENTE o primeiro PST — nunca declara completude (#TotalRows), então o
        // segundo PST canônico simplesmente não é coberto (aceito como evidência PARCIAL pelo Passo 1).
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(coveredRemoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);

        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var pstItems = await Assessments().GetPstItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None);

        Assert.Equal(2, pstItems.Count);
        var missing = Assert.Single(pstItems, item => item.RemoteName.Value != coveredRemoteName);
        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, missing.Disposition);
        Assert.Null(missing.ObservedStatus);
        var covered = Assert.Single(pstItems, item => item.RemoteName.Value == coveredRemoteName);
        Assert.Equal(ReconciliationDisposition.MatchedWithinEvidence, covered.Disposition);
    }

    [Fact]
    public async Task EvaluateNeverConvertsAnUnreportedCounterIntoZeroOrMatch()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-unknown.pst", "recon-unknown@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);

        // Relatório sem as colunas de contador: status conhecido, mas contadores Unknown/NotReported.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            $"RemotePstName,Status\n{remoteName},Succeeded\n");
        await ImportReportUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, bytes, "operator", CancellationToken.None);

        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var item = Assert.Single(await Assessments().GetPstItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None));

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.Null(item.ImportedItemCount);
        Assert.Null(item.ImportedSizeBytes);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("SkippedOrCorrupted")]
    public async Task EvaluateMarksAConcreteObservedFailureAsMismatch(string status)
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-mismatch.pst", "recon-mismatch@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);

        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, status, 0, 0)]), "operator", CancellationToken.None);

        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var item = Assert.Single(await Assessments().GetPstItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None));

        Assert.Equal(ReconciliationDisposition.Mismatch, item.Disposition);
    }

    // ---- Archive before/after: ausência de um dos lados ----

    [Fact]
    public async Task EvaluateMarksAnArchiveAsIncompleteEvidenceWhenOnlyBeforeWasCaptured()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-before-only.pst", "recon-before-only@contoso.com"));
        var archive = entries[0].Entry.Archive.Identity;
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(10, 1000)))
            .ExecuteBeforeImportAsync(scope, wave.Id, archive, CorrelationId.New(), CancellationToken.None);

        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var item = Assert.Single(await Assessments().GetArchiveItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None));

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.True(item.BeforeCaptured);
        Assert.False(item.AfterCaptured);
        Assert.Null(item.ItemCountDelta);
    }

    [Fact]
    public async Task EvaluateMarksAnArchiveAsIncompleteEvidenceWhenNoSnapshotWasEverCaptured()
    {
        var (scope, wave, _, plannedJobName) = await SeedPlannedWaveAsync(("recon-no-stats.pst", "recon-no-stats@contoso.com"));

        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var item = Assert.Single(await Assessments().GetArchiveItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None));

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.False(item.BeforeCaptured);
        Assert.False(item.AfterCaptured);
    }

    // ---- Drift/staleness (item 4/12) ----

    [Fact]
    public async Task EvaluateFailsClosedWhenTheCanonicalChainDriftedSinceThePreviousAssessmentAndNeverTreatsTheOldOneAsCanonical()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-drift.pst", "recon-drift@contoso.com"));
        var first = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, first.AssessmentVersion);

        // Drift real na cadeia canônica DEPOIS da avaliação — SEM regenerar/republicar o mapping (mesma
        // técnica de PurviewImportJobIntegrationTests.ImportReportReplayFailsClosedWhenCanonicalChainDriftedSinceThePreviousImport).
        await SeedPrecheckAsync(scope, entries[0].Entry, MailboxArchiveStatus.Disabled);

        await Assert.ThrowsAsync<PurviewImportJobPrerequisiteException>(() =>
            ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None));

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_assessments WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    // ---- Idempotência/versionamento (itens 10-11) ----

    [Fact]
    public async Task EvaluateIsIdempotentWhenNoSourceEvidenceChangedBetweenCalls()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-idempotent.pst", "recon-idempotent@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);

        var first = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var second = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.AssessmentVersion, second.AssessmentVersion);
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_assessments WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EvaluateProducesANewVersionWhenTheObservedServiceResultGenuinelyChangesWithoutLosingThePreviousVersion()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-newversion.pst", "recon-newversion@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        var first = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        // Mudança REAL de evidência observada: uma nova versão do relatório com contadores diferentes
        // (conteúdo bruto diferente ⇒ nova versão de PurviewServiceResultReportEvidence pelo Passo 1).
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 999, 999999)]), "operator", CancellationToken.None);
        var second = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(1, first.AssessmentVersion);
        Assert.Equal(2, second.AssessmentVersion);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_assessments WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(2, count);

        // A versão antiga permanece legível/intacta (append-only) mesmo depois da nova versão existir.
        var oldItems = await Assessments().GetPstItemsAsync(scope, wave.Id, plannedJobName, first.AssessmentVersion, CancellationToken.None);
        Assert.Equal(10, Assert.Single(oldItems).ImportedItemCount);
    }

    // ---- Concorrência (chamadas diretas à store, mesmo padrão de SqlExoArchiveStatisticsStore) ----

    [Fact]
    public async Task PersistConvergesUnderFiveIdenticalEvidenceSetsInsteadOfDuplicating()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-concurrency-same.pst", "recon-concurrency-same@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);

        var versions = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
            versions.Add(assessment.AssessmentVersion);
        }

        Assert.All(versions, version => Assert.Equal(1, version));
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_assessments WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PersistProducesDistinctVersionsForGenuinelyDifferentEvidenceSetsWithoutLosingEither()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-concurrency-distinct.pst", "recon-concurrency-distinct@contoso.com"));
        var archive = entries[0].Entry.Archive.Identity;
        var store = Assessments();
        var fingerprintA = DeterministicHash.Compute(["fingerprint-a"]);
        var fingerprintB = DeterministicHash.Compute(["fingerprint-b"]);

        var first = await store.PersistAsync(
            scope, wave.Id, plannedJobName, fingerprintA, null, null, [], [], [], CorrelationId.New(), Clock.UtcNow, fence: null, CancellationToken.None);
        var second = await store.PersistAsync(
            scope, wave.Id, plannedJobName, fingerprintB, null, null, [], [], [], CorrelationId.New(), Clock.UtcNow, fence: null, CancellationToken.None);

        Assert.NotEqual(first.AssessmentVersion, second.AssessmentVersion);
        _ = archive;
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_assessments WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(2, count);
    }

    // ---- Tampering direto no SQL ----

    [Fact]
    public async Task GetLatestFailsClosedWhenAPersistedAssessmentHeaderFieldIsTamperedDirectlyInSql()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-tamper-header.pst", "recon-tamper-header@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_assessments SET pst_item_count = 999 WHERE wave_id = @wave;", ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() =>
            Assessments().GetLatestAsync(scope, wave.Id, plannedJobName, CancellationToken.None));
    }

    [Fact]
    public async Task GetPstItemsFailsClosedWhenAChildRowIsTamperedDirectlyInSql()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-tamper-pst-item.pst", "recon-tamper-pst-item@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        // O item seedado ("Succeeded" com contadores presentes) já persiste como disposition
        // MatchedWithinEvidence (0) — usar esse MESMO valor aqui não seria tampering algum (a
        // linha permaneceria idêntica e o hash agregado bateria). Adultera para Mismatch (1) para
        // realmente divergir do que foi persistido e provar que ValidateAndLoadPstItemsAsync
        // detecta a adulteração via pst_items_sha256.
        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_pst_items SET disposition = 1 WHERE wave_id = @wave;", ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() =>
            Assessments().GetPstItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None));
    }

    [Fact]
    public async Task GetArchiveItemsFailsClosedWhenAnExtraChildRowIsInsertedDirectlyInSql()
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(("recon-tamper-archive-extra.pst", "recon-tamper-archive-extra@contoso.com"));
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            """
            INSERT INTO dbo.purview_reconciliation_archive_items
                (wave_id, attempt_sequence, assessment_version, tenant_id, project_id, archive_identity, disposition, before_captured, after_captured)
            SELECT wave_id, attempt_sequence, assessment_version, tenant_id, project_id, N'INJECTED@CONTOSO.COM', 0, 0, 0
            FROM dbo.purview_reconciliation_assessments WHERE wave_id = @wave;
            """,
            ("@wave", wave.Id.Value));
        _ = entries;

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() =>
            Assessments().GetArchiveItemsAsync(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, CancellationToken.None));
    }

    private async Task ExecuteAdminSqlAsync(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
