using System.Data;
using ArchiveBridge.Application.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I6-005 (SQL Server real) — <see cref="CaptureExoArchiveStatisticsUseCase"/> e
/// <see cref="SqlExoArchiveStatisticsStore"/>: captura read-only BeforeImport/AfterImport, resolução
/// server-side anti-IDOR do archive, gate de completude do AfterImport (reaproveitando
/// <see cref="EvaluatePurviewServiceResultCompletenessUseCase"/> do Passo 1), Unknown/NotReported nunca
/// vira zero/false, canonicalização/hash de pastas independente de ordem, convergência idempotente e
/// tampering direto no SQL (header e estatísticas de pasta filhas).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ExoArchiveStatisticsIntegrationTests(SqlServerFixture fixture)
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

    private FileSystemMappingArtifactStore Artifacts() =>
        new(Path.Combine(fixture.ArtifactRoot, "exo-archive-statistics-" + Guid.NewGuid().ToString("N")));

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
        new(Slice2Support.WaveStore(fixture), adapter, Snapshots(), CompletenessUseCase(), Clock);

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

    /// <summary>Onda com 1 entrada resolvida/aprovada e archive Active — piso mínimo para captura EXO stats.</summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, WaveEntry Entry)> SeedResolvedWaveAsync(
        string name, string mailbox, TenantScope? scopeOverride = null)
    {
        var scope = scopeOverride ?? SqlServerFixture.NewScope();
        if (scopeOverride is null)
        {
            await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        }

        var execution = await RegisterAndExecuteAsync(scope, name);
        var entry = Slice2Support.Entry(name, mailbox, execution.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entry])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);

        return (scope, wave, entry);
    }

    /// <summary>
    /// Onda com plano de import job já criado (bindings/upload verificado/mapping publicado) mas SEM
    /// nenhum service result report importado ainda — completude permanece <c>Incomplete</c>, o piso
    /// exato para provar que o gate do AfterImport bloqueia sem sondar o adapter.
    /// </summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, WaveEntry Entry, PartitionExecutionRecord Execution, PurviewImportJobName PlannedJobName)>
        SeedPlannedWaveAsync(string name, string mailbox)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var execution = await RegisterAndExecuteAsync(scope, name);
        var entry = Slice2Support.Entry(name, mailbox, execution.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entry])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);
        await MarkUploadVerifiedAsync(scope, wave, [execution]);
        await GenerateMappingUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        return (scope, wave, entry, execution, plan.PlannedJobName);
    }

    /// <summary>Onda que atinge <see cref="PurviewServiceResultCompletenessOutcome.CompleteForProviderEvidence"/> — o piso para AfterImport.</summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, WaveEntry Entry, PurviewImportJobName PlannedJobName)> SeedAfterImportEligibleWaveAsync(
        string name, string mailbox)
    {
        var (scope, wave, entry, execution, plannedJobName) = await SeedPlannedWaveAsync(name, mailbox);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;
        var reportBytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            $"RemotePstName,Status,ImportedItemCount,ImportedSizeBytes\n{remoteName},Succeeded,10,2048\n");
        await ImportReportUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, reportBytes, "operator", CancellationToken.None);

        return (scope, wave, entry, plannedJobName);
    }

    // Fixos (nunca Guid.NewGuid()/DateTimeOffset.UtcNow por chamada): dois capturas com o mesmo conteúdo
    // lógico precisam produzir o MESMO ObservationHash para os testes de idempotência/convergência —
    // um GUID/timestamp fresco a cada chamada quebraria a convergência mesmo com conteúdo "igual".
    private static readonly Guid FixedExchangeGuid = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid FixedArchiveGuid = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset FixedObservedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedLastLogon = new(2025, 12, 31, 12, 0, 0, TimeSpan.Zero);

    private static ExoArchiveStatisticsObservation Observation(
        long? itemCount = 1000,
        long? totalSizeBytes = 10_000_000,
        long? deletedSizeBytes = 0,
        DateTimeOffset? lastLogon = null,
        bool? retentionHold = false,
        bool? litigationHold = false,
        bool? autoExpanding = false,
        MailboxArchiveStatus status = MailboxArchiveStatus.Active,
        IReadOnlyList<ExoArchiveFolderStatisticObservation>? folders = null,
        DateTimeOffset? observedAt = null) =>
        new(
            status, FixedExchangeGuid, FixedArchiveGuid, itemCount, totalSizeBytes, deletedSizeBytes,
            lastLogon ?? FixedLastLogon, retentionHold, litigationHold, autoExpanding,
            folders ?? [new ExoArchiveFolderStatisticObservation("/Top of Information Store/Inbox", "Inbox", 10, 10, 2048, 2048, null, null)],
            observedAt ?? FixedObservedAt);

    // ---- BeforeImport ----

    [Fact]
    public async Task BeforeImportCapturesAndPersistsReadOnlyStatisticsForAnAuthorizedArchive()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-happy.pst", "before-happy@contoso.com");
        var adapter = new FakeExoArchiveStatisticsAdapter(Observation());

        var snapshot = await CaptureUseCase(adapter).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(ExoStatisticsPhase.BeforeImport, snapshot.Phase);
        Assert.Equal(1, snapshot.SnapshotVersion);
        Assert.Equal(1, adapter.ObserveCallCount);
        Assert.Equal(ExoStatisticsPhase.BeforeImport, adapter.LastObservedPhase);

        var folders = await Snapshots().GetFoldersAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, 1, CancellationToken.None);
        Assert.Equal(1, snapshot.FolderCount);
        Assert.Equal("/Top of Information Store/Inbox", Assert.Single(folders).FolderPath);
    }

    [Fact]
    public async Task BeforeImportIsIdempotentForTheSameLogicalObservation()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-idempotent.pst", "before-idempotent@contoso.com");
        var observedAt = DateTimeOffset.UtcNow;
        var useCase = CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(observedAt: observedAt)));

        var first = await useCase.ExecuteBeforeImportAsync(scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);
        var secondAdapter = new FakeExoArchiveStatisticsAdapter(Observation(observedAt: observedAt));
        var second = await CaptureUseCase(secondAdapter).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.SnapshotVersion, second.SnapshotVersion);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_exo_archive_statistics_snapshots WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task BeforeImportProducesANewVersionForGenuinelyDifferentObservation()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-newversion.pst", "before-newversion@contoso.com");

        var first = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(itemCount: 100))).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);
        var second = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(itemCount: 200))).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(1, first.SnapshotVersion);
        Assert.Equal(2, second.SnapshotVersion);
        Assert.Equal(200, second.ItemCount);
    }

    [Fact]
    public async Task BeforeImportFailsClosedForAnArchiveOutsideTheWaveSelectionAndNeverProbesTheAdapter()
    {
        var (scope, wave, _) = await SeedResolvedWaveAsync("before-idor.pst", "before-idor@contoso.com");
        var adapter = new FakeExoArchiveStatisticsAdapter(Observation());

        await Assert.ThrowsAsync<ExoArchiveStatisticsSourceNotFoundException>(() => CaptureUseCase(adapter).ExecuteBeforeImportAsync(
            scope, wave.Id, new TargetArchiveId("attacker-arbitrary@contoso.com"), CorrelationId.New(), CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task BeforeImportFailsClosedForAWaveOutsideTheCallersScopeAndNeverProbesTheAdapter()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-cross-scope.pst", "before-cross-scope@contoso.com");
        var otherScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        var adapter = new FakeExoArchiveStatisticsAdapter(Observation());

        await Assert.ThrowsAsync<ExoArchiveStatisticsSourceNotFoundException>(() => CaptureUseCase(adapter).ExecuteBeforeImportAsync(
            otherScope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task BeforeImportPreservesUnknownFieldsAsNullNeverAsZeroOrFalse()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-unknown.pst", "before-unknown@contoso.com");
        // Construído diretamente (sem o helper Observation()): o helper usa "??" para preencher um
        // default sensato quando o chamador não personaliza um campo, o que não distingue "omitido" de
        // "explicitamente null" — aqui o teste exige LastLogonTimeUtc REALMENTE null (Unknown/NotReported).
        var observation = new ExoArchiveStatisticsObservation(
            MailboxArchiveStatus.Unknown, ExchangeGuid: null, ArchiveGuid: null, ItemCount: null, TotalItemSizeBytes: null,
            TotalDeletedItemSizeBytes: null, LastLogonTimeUtc: null, RetentionHoldEnabled: null, LitigationHoldEnabled: null,
            AutoExpandingArchiveEnabled: null,
            Folders: [new ExoArchiveFolderStatisticObservation("/Inbox", "Inbox", null, null, null, null, null, null)],
            ObservedAtUtc: FixedObservedAt);
        var adapter = new FakeExoArchiveStatisticsAdapter(observation);

        var snapshot = await CaptureUseCase(adapter).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        Assert.Null(snapshot.ItemCount);
        Assert.Null(snapshot.TotalItemSizeBytes);
        Assert.Null(snapshot.TotalDeletedItemSizeBytes);
        Assert.Null(snapshot.LastLogonTimeUtc);
        Assert.Null(snapshot.RetentionHoldEnabled);
        Assert.Null(snapshot.LitigationHoldEnabled);
        Assert.Null(snapshot.AutoExpandingArchiveEnabled);
        Assert.Equal(MailboxArchiveStatus.Unknown, snapshot.ArchiveStatus);

        var reread = await Snapshots().GetLatestAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, CancellationToken.None);
        Assert.Null(reread!.ItemCount);
        var folder = Assert.Single(await Snapshots().GetFoldersAsync(
            scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, reread.SnapshotVersion, CancellationToken.None));
        Assert.Null(folder.ItemsInFolder);
        Assert.Null(folder.FolderSizeBytes);
    }

    [Fact]
    public async Task BeforeImportFolderHashConvergesRegardlessOfAdapterFolderOrder()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-folder-order.pst", "before-folder-order@contoso.com");
        var observedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<ExoArchiveFolderStatisticObservation> inOrderA =
        [
            new("/A", "User Created", 1, 1, 100, 100, null, null),
            new("/B", "User Created", 2, 2, 200, 200, null, null),
        ];
        IReadOnlyList<ExoArchiveFolderStatisticObservation> inOrderB = [inOrderA[1], inOrderA[0]];

        var first = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(folders: inOrderA, observedAt: observedAt)))
            .ExecuteBeforeImportAsync(scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);
        var second = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(folders: inOrderB, observedAt: observedAt)))
            .ExecuteBeforeImportAsync(scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.SnapshotVersion, second.SnapshotVersion);
        Assert.Equal(first.FoldersSha256, second.FoldersSha256);
    }

    [Fact]
    public async Task BeforeImportFailsClosedWhenAdapterReturnsADuplicateFolderPath()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("before-dup-folder.pst", "before-dup-folder@contoso.com");
        IReadOnlyList<ExoArchiveFolderStatisticObservation> duplicated =
        [
            new("/Inbox", "Inbox", 1, 1, 1, 1, null, null),
            new("/Inbox", "Inbox", 2, 2, 2, 2, null, null),
        ];
        var adapter = new FakeExoArchiveStatisticsAdapter(Observation(folders: duplicated));

        await Assert.ThrowsAsync<ExoArchiveStatisticsValidationException>(() => CaptureUseCase(adapter).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None));
    }

    // ---- AfterImport ----

    [Fact]
    public async Task AfterImportFailsClosedBeforeImportCompletionEvidenceExistsAndNeverProbesTheAdapter()
    {
        var (scope, wave, entry, _, plannedJobName) = await SeedPlannedWaveAsync("after-no-evidence.pst", "after-no-evidence@contoso.com");
        var adapter = new FakeExoArchiveStatisticsAdapter(Observation());

        await Assert.ThrowsAsync<ExoArchiveStatisticsPrerequisiteException>(() => CaptureUseCase(adapter).ExecuteAfterImportAsync(
            scope, wave.Id, entry.Archive.Identity, plannedJobName, CorrelationId.New(), CancellationToken.None));
        Assert.Equal(0, adapter.ObserveCallCount);
    }

    [Fact]
    public async Task AfterImportSucceedsOnceProviderEvidenceIsCompleteForAllCanonicalPsts()
    {
        var (scope, wave, entry, plannedJobName) =
            await SeedAfterImportEligibleWaveAsync("after-happy.pst", "after-happy@contoso.com");
        var adapter = new FakeExoArchiveStatisticsAdapter(Observation());

        var snapshot = await CaptureUseCase(adapter).ExecuteAfterImportAsync(
            scope, wave.Id, entry.Archive.Identity, plannedJobName, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(ExoStatisticsPhase.AfterImport, snapshot.Phase);
        Assert.Equal(1, adapter.ObserveCallCount);
        Assert.Equal(ExoStatisticsPhase.AfterImport, adapter.LastObservedPhase);
    }

    [Fact]
    public async Task BeforeAndAfterImportSnapshotsForTheSameArchiveAreIndependentlyVersioned()
    {
        var (scope, wave, entry, plannedJobName) =
            await SeedAfterImportEligibleWaveAsync("before-after-independent.pst", "before-after-independent@contoso.com");

        var before = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(itemCount: 10)))
            .ExecuteBeforeImportAsync(scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);
        var after = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(itemCount: 20)))
            .ExecuteAfterImportAsync(scope, wave.Id, entry.Archive.Identity, plannedJobName, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(1, before.SnapshotVersion);
        Assert.Equal(1, after.SnapshotVersion);
        Assert.Equal(10, before.ItemCount);
        Assert.Equal(20, after.ItemCount);
    }

    // ---- Concorrência (chamadas diretas à store, mesmo padrão de SqlPurviewServiceResultReportStore) ----

    [Fact]
    public async Task PersistConvergesUnderConcurrentIdenticalObservationsInsteadOfDuplicating()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("concurrency-identical.pst", "concurrency-identical@contoso.com");
        var store = Snapshots();
        var folders = new[] { new ExoArchiveFolderStatistic("/Inbox", "Inbox", 1, 1, 1, 1, null, null) };
        var observedAt = DateTimeOffset.UtcNow;
        var correlation = CorrelationId.New();

        var first = await store.PersistAsync(
            scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, MailboxArchiveStatus.Active, Guid.NewGuid(),
            Guid.NewGuid(), 100, 1000, 0, null, false, false, false, folders, observedAt, correlation, Clock.UtcNow, fence: null,
            CancellationToken.None);
        var second = await store.PersistAsync(
            scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, first.ArchiveStatus, first.ExchangeGuid,
            first.ArchiveGuid, first.ItemCount, first.TotalItemSizeBytes, first.TotalDeletedItemSizeBytes, first.LastLogonTimeUtc,
            first.RetentionHoldEnabled, first.LitigationHoldEnabled, first.AutoExpandingArchiveEnabled, folders, observedAt,
            correlation, Clock.UtcNow, fence: null, CancellationToken.None);

        Assert.Equal(first.SnapshotVersion, second.SnapshotVersion);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_exo_archive_statistics_snapshots WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PersistProducesDistinctVersionsForConcurrentDifferentObservationsWithoutLosingEither()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("concurrency-distinct.pst", "concurrency-distinct@contoso.com");
        var store = Snapshots();
        var folders = new[] { new ExoArchiveFolderStatistic("/Inbox", "Inbox", 1, 1, 1, 1, null, null) };
        var observedAt = DateTimeOffset.UtcNow;

        var first = await store.PersistAsync(
            scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, MailboxArchiveStatus.Active, Guid.NewGuid(),
            Guid.NewGuid(), 100, 1000, 0, null, false, false, false, folders, observedAt, CorrelationId.New(), Clock.UtcNow,
            fence: null, CancellationToken.None);
        var second = await store.PersistAsync(
            scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, MailboxArchiveStatus.Active, Guid.NewGuid(),
            Guid.NewGuid(), 200, 1000, 0, null, false, false, false, folders, observedAt, CorrelationId.New(), Clock.UtcNow,
            fence: null, CancellationToken.None);

        Assert.NotEqual(first.SnapshotVersion, second.SnapshotVersion);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_exo_archive_statistics_snapshots WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(2, count);
    }

    // ---- Tampering direto no SQL ----

    [Fact]
    public async Task GetLatestFailsClosedWhenAPersistedSnapshotHeaderFieldIsTamperedDirectlyInSql()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("tamper-header.pst", "tamper-header@contoso.com");
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation())).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_exo_archive_statistics_snapshots SET item_count = 999999 WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<ExoArchiveStatisticsIntegrityViolationException>(() =>
            Snapshots().GetLatestAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, CancellationToken.None));
    }

    [Fact]
    public async Task GetFoldersFailsClosedWhenAPersistedFolderRowIsTamperedDirectlyInSql()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("tamper-folder-row.pst", "tamper-folder-row@contoso.com");
        var snapshot = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation())).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_exo_archive_folder_statistics SET items_in_folder = 999999 WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<ExoArchiveStatisticsIntegrityViolationException>(() =>
            Snapshots().GetFoldersAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, snapshot.SnapshotVersion, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenAFolderRowIsDeletedDirectlyInSql()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("tamper-folder-delete.pst", "tamper-folder-delete@contoso.com");
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation())).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "DELETE FROM dbo.purview_exo_archive_folder_statistics WHERE wave_id = @wave;", ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<ExoArchiveStatisticsIntegrityViolationException>(() =>
            Snapshots().GetLatestAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenAnExtraFolderRowIsInsertedDirectlyInSql()
    {
        var (scope, wave, entry) = await SeedResolvedWaveAsync("tamper-folder-insert.pst", "tamper-folder-insert@contoso.com");
        var snapshot = await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation())).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            """
            INSERT INTO dbo.purview_exo_archive_folder_statistics
                (wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id, folder_path, folder_type)
            VALUES (@wave, @archive, 0, @version, @tenant, @project, N'/Injected', N'Other');
            """,
            ("@wave", wave.Id.Value), ("@archive", entry.Archive.Identity.Value), ("@version", snapshot.SnapshotVersion),
            ("@tenant", scope.Tenant.Value), ("@project", scope.Project.Value));

        await Assert.ThrowsAsync<ExoArchiveStatisticsIntegrityViolationException>(() =>
            Snapshots().GetLatestAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.BeforeImport, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenThePhaseColumnOfAPersistedSnapshotIsTamperedDirectlyInSql()
    {
        // Adulterar phase muda a identidade lógica do registro (BeforeImport <-> AfterImport) sem tocar o
        // observation_hash/snapshot_hash calculados sobre o valor ORIGINAL — deve ser detectado fail-closed
        // na revalidação, nunca lido como um AfterImport (ou BeforeImport) canônico legítimo. Sem pastas
        // filhas (FolderCount 0) para que a mudança de phase no header não colida com a FK das estatísticas
        // de pasta (que também referencia phase) — o alvo deste teste é o header, não a cascata de FK.
        var (scope, wave, entry) = await SeedResolvedWaveAsync("tamper-phase.pst", "tamper-phase@contoso.com");
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(folders: []))).ExecuteBeforeImportAsync(
            scope, wave.Id, entry.Archive.Identity, CorrelationId.New(), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_exo_archive_statistics_snapshots SET phase = 1 WHERE wave_id = @wave AND phase = 0;",
            ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<ExoArchiveStatisticsIntegrityViolationException>(() =>
            Snapshots().GetLatestAsync(scope, wave.Id, entry.Archive.Identity, ExoStatisticsPhase.AfterImport, CancellationToken.None));
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

/// <summary>Duplo de teste da porta <see cref="IExoArchiveStatisticsAdapter"/> — determinístico, sem EXO/Graph.</summary>
internal sealed class FakeExoArchiveStatisticsAdapter(ExoArchiveStatisticsObservation observation) : IExoArchiveStatisticsAdapter
{
    /// <summary>Quantas vezes o adapter foi sondado — usado para provar que falhas fail-closed nunca sondam.</summary>
    public int ObserveCallCount { get; private set; }

    /// <summary>O archive efetivamente recebido na última sondagem, para provar que é o CANÔNICO resolvido server-side.</summary>
    public ArchiveRef? LastObservedArchive { get; private set; }

    /// <summary>A fase efetivamente recebida na última sondagem.</summary>
    public ExoStatisticsPhase? LastObservedPhase { get; private set; }

    public Task<ExoArchiveStatisticsObservation> ObserveAsync(
        TenantScope scope, ArchiveRef archive, ExoStatisticsPhase phase, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ObserveCallCount++;
        LastObservedArchive = archive;
        LastObservedPhase = phase;
        return Task.FromResult(observation);
    }
}
