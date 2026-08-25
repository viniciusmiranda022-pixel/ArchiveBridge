using System.Data;
using System.Text;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
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
/// AB-I6-001 (SQL Server real) — fundação de evidência do import job do Purview: planejamento
/// determinístico do nome do job, registro de observações do provider (idempotência/reassociação
/// fail-closed), importação bounded/correlacionada do service result report, e a avaliação de completude
/// da evidência do provider. Cobre anti-IDOR, drift entre mapping publicado e evidência atual,
/// concorrência/idempotência e tampering direto no SQL.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PurviewImportJobIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private SqlPurviewUploadRequestStore UploadRequests() => new(fixture.Factory, Clock);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private SqlMailboxPrecheckStore Prechecks() => new(fixture.Factory);

    private SqlPurviewMappingCsvStore MappingStore() => new(fixture.Factory, Clock);

    private SqlPurviewImportJobStore Jobs() => new(fixture.Factory);

    private SqlPurviewServiceResultReportStore Reports() => new(fixture.Factory);

    private FileSystemMappingArtifactStore Artifacts() =>
        new(Path.Combine(fixture.ArtifactRoot, "purview-import-job-" + Guid.NewGuid().ToString("N")));

    private CreateWavePartitionOutputBindingUseCase BindingUseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    private ResolvePurviewMappingEvidenceUseCase EvidenceResolver() => new(
        Slice2Support.WaveStore(fixture), Bindings(), Slice4bPstProcessingSupport.ExecutionStore(fixture),
        UploadRequests(), UploadAttempts(), Prechecks());

    private GeneratePurviewMappingCsvUseCase GenerateMappingUseCase() => new(EvidenceResolver(), MappingStore(), Artifacts(), Clock);

    private PlanPurviewImportJobUseCase PlanUseCase() => new(EvidenceResolver(), MappingStore(), Jobs(), Clock);

    private RegisterPurviewImportJobObservationUseCase ObservationUseCase() => new(EvidenceResolver(), MappingStore(), Jobs(), Clock);

    private ImportPurviewServiceResultReportUseCase ImportReportUseCase() => new(EvidenceResolver(), MappingStore(), Jobs(), Reports(), Clock);

    private EvaluatePurviewServiceResultCompletenessUseCase CompletenessUseCase() =>
        new(EvidenceResolver(), MappingStore(), Jobs(), Reports());

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
    /// Constrói o cenário COMPLETO exigido pelos pré-requisitos do AB-I6-001 (item 3): onda aprovada, 1
    /// entrada, PST executado, vínculo canônico, precheck Active, upload verificado E mapping CSV
    /// gerado/publicado (Usable) coerente com toda a evidência — o piso mínimo para planejar um import job.
    /// </summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, WaveEntry Entry, PartitionExecutionRecord Execution)> SeedPlannableWaveAsync(
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

        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);
        await MarkUploadVerifiedAsync(scope, wave, [execution]);
        await GenerateMappingUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        return (scope, wave, entry, execution);
    }

    // ---- PlanPurviewImportJobUseCase ----

    [Fact]
    public async Task PlanProducesADeterministicLowercaseJobNameLinkedToTheWave()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("plan-happy.pst", "plan-happy@contoso.com");

        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        Assert.Equal(1, plan.AttemptSequence);
        Assert.Matches("^[a-z0-9_-]+$", plan.PlannedJobName.Value);
        Assert.Equal(wave.Id, plan.Wave);
    }

    [Fact]
    public async Task PlanIsIdempotentWhenTheCanonicalEvidenceHasNotChanged()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("plan-idempotent.pst", "plan-idempotent@contoso.com");
        var useCase = PlanUseCase();

        var first = await useCase.ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        Assert.Equal(first.PlannedJobName, second.PlannedJobName);
        Assert.Equal(first.AttemptSequence, second.AttemptSequence);

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_import_job_plans WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PlanFailsClosedForAWaveOutsideTheCallersScope()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("plan-idor.pst", "plan-idor@contoso.com");
        var otherScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));

        await Assert.ThrowsAsync<PurviewImportJobSourceNotFoundException>(() =>
            PlanUseCase().ExecuteAsync(otherScope, wave.Id, "operator", CancellationToken.None));
    }

    [Fact]
    public async Task PlanFailsClosedWhenNoMappingCsvHasEverBeenPublished()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var execution = await RegisterAndExecuteAsync(scope, "plan-no-mapping.pst");
        var entry = Slice2Support.Entry("plan-no-mapping.pst", "plan-no-mapping@contoso.com", execution.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entry])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);
        await MarkUploadVerifiedAsync(scope, wave, [execution]);
        // Nenhuma chamada a GenerateMappingUseCase — nenhum mapping canônico publicado ainda.

        await Assert.ThrowsAsync<PurviewImportJobPrerequisiteException>(() =>
            PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None));
    }

    [Fact]
    public async Task PlanFailsClosedWhenAnAdditionalBindingWasCreatedAfterTheMappingWasPublished()
    {
        // Drift real: um novo vínculo (novo PST) é adicionado à onda DEPOIS do mapping ter sido publicado
        // — a evidência canônica atual não coincide mais com o fingerprint do mapping Usable.
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var executionA = await RegisterAndExecuteAsync(scope, "drift-plan-a.pst");
        var executionB = await RegisterAndExecuteAsync(scope, "drift-plan-b.pst");
        var entryA = Slice2Support.Entry("drift-plan-a.pst", "drift-plan-a@contoso.com", executionA.OutputSizeBytes);
        var entryB = Slice2Support.Entry("drift-plan-b.pst", "drift-plan-b@contoso.com", executionB.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entryA, entryB])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entryA), executionA.Plan, executionA.Part, CorrelationId.New()),
            CancellationToken.None);
        await SeedPrecheckAsync(scope, entryA, MailboxArchiveStatus.Active);
        await SeedPrecheckAsync(scope, entryB, MailboxArchiveStatus.Active);
        await MarkUploadVerifiedAsync(scope, wave, [executionA]);
        await GenerateMappingUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        // Vínculo B criado DEPOIS da publicação do mapping (que só cobria A) — drift.
        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entryB), executionB.Plan, executionB.Part, CorrelationId.New()),
            CancellationToken.None);

        await Assert.ThrowsAsync<PurviewImportJobPrerequisiteException>(() =>
            PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None));
    }

    [Fact]
    public async Task PlanProducesANewAttemptWhenTheMappingWasRegeneratedAfterARealEvidenceChange()
    {
        var (scope, wave, entry, _) = await SeedPlannableWaveAsync("plan-new-attempt.pst", "plan-new-attempt@contoso.com");
        var first = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        // Mudança REAL de evidência (precheck) seguida de regeneração do mapping — evidência nova e coerente.
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Disabled);
        await GenerateMappingUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        var second = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        Assert.Equal(2, second.AttemptSequence);
        Assert.NotEqual(first.PlannedJobName, second.PlannedJobName);
    }

    // ---- RegisterPurviewImportJobObservationUseCase ----

    [Fact]
    public async Task RegisterObservationPersistsAndIsRetrievableAsTheLatest()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("obs-happy.pst", "obs-happy@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var providerId = PurviewProviderOperationId.Create("purview-obs-happy-001");

        var observation = await ObservationUseCase().ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, providerId, PurviewImportJobObservedStatus.JobCreated,
            Clock.UtcNow, "operator@contoso.com", CancellationToken.None);

        Assert.Equal(PurviewImportJobObservedStatus.JobCreated, observation.ObservedStatus);
        var latest = await Jobs().GetLatestObservationAsync(scope, wave.Id, plan.PlannedJobName, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(providerId.Value, latest!.ProviderOperationId.Value);
    }

    [Fact]
    public async Task RegisterObservationIsIdempotentForAnIdenticalReplay()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("obs-idempotent.pst", "obs-idempotent@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var providerId = PurviewProviderOperationId.Create("purview-obs-idempotent-001");
        var observedAt = Clock.UtcNow;
        var useCase = ObservationUseCase();

        await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, providerId, PurviewImportJobObservedStatus.JobCreated, observedAt, "operator@contoso.com",
            CancellationToken.None);
        await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, providerId, PurviewImportJobObservedStatus.JobCreated, observedAt, "operator@contoso.com",
            CancellationToken.None);

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_import_job_observations WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegisterObservationAllowsProgressionThroughDistinctStatusesForTheSameProviderId()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("obs-progression.pst", "obs-progression@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var providerId = PurviewProviderOperationId.Create("purview-obs-progression-001");
        var useCase = ObservationUseCase();

        await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, providerId, PurviewImportJobObservedStatus.JobCreated, Clock.UtcNow, "operator@contoso.com",
            CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, providerId, PurviewImportJobObservedStatus.AnalysisCompleted, Clock.UtcNow, "operator@contoso.com",
            CancellationToken.None);

        Assert.Equal(PurviewImportJobObservedStatus.AnalysisCompleted, second.ObservedStatus);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_import_job_observations WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RegisterObservationFailsClosedWhenReassociatingTheSamePlanToADifferentProviderId()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("obs-reassoc.pst", "obs-reassoc@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var useCase = ObservationUseCase();

        await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, PurviewProviderOperationId.Create("purview-obs-reassoc-original"),
            PurviewImportJobObservedStatus.JobCreated, Clock.UtcNow, "operator@contoso.com", CancellationToken.None);

        await Assert.ThrowsAsync<PurviewImportJobIdentityConflictException>(() =>
            useCase.ExecuteAsync(
                scope, wave.Id, plan.PlannedJobName, PurviewProviderOperationId.Create("purview-obs-reassoc-DIFFERENT"),
                PurviewImportJobObservedStatus.AnalysisCompleted, Clock.UtcNow, "operator@contoso.com", CancellationToken.None));
    }

    [Fact]
    public async Task RegisterObservationFailsClosedWhenTheSameProviderIdIsClaimedByADifferentPlanInTheSameScope()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var (_, waveA, _, _) = await SeedPlannableWaveAsync("obs-cross-a.pst", "obs-cross-a@contoso.com", scope);
        var (_, waveB, _, _) = await SeedPlannableWaveAsync("obs-cross-b.pst", "obs-cross-b@contoso.com", scope);
        var planA = await PlanUseCase().ExecuteAsync(scope, waveA.Id, "operator", CancellationToken.None);
        var planB = await PlanUseCase().ExecuteAsync(scope, waveB.Id, "operator", CancellationToken.None);
        var sharedProviderId = PurviewProviderOperationId.Create("purview-obs-cross-shared");
        var useCase = ObservationUseCase();

        await useCase.ExecuteAsync(
            scope, waveA.Id, planA.PlannedJobName, sharedProviderId, PurviewImportJobObservedStatus.JobCreated, Clock.UtcNow,
            "operator@contoso.com", CancellationToken.None);

        await Assert.ThrowsAsync<PurviewImportJobIdentityConflictException>(() =>
            useCase.ExecuteAsync(
                scope, waveB.Id, planB.PlannedJobName, sharedProviderId, PurviewImportJobObservedStatus.JobCreated, Clock.UtcNow,
                "operator@contoso.com", CancellationToken.None));
    }

    [Fact]
    public async Task RegisterObservationConvergesUnderConcurrentIdenticalCalls()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("obs-concurrency.pst", "obs-concurrency@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var providerId = PurviewProviderOperationId.Create("purview-obs-concurrency-001");
        var observedAt = Clock.UtcNow;

        var tasks = Enumerable.Range(0, 5).Select(_ => ObservationUseCase().ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, providerId, PurviewImportJobObservedStatus.JobCreated, observedAt, "operator@contoso.com",
            CancellationToken.None));
        await Task.WhenAll(tasks);

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_import_job_observations WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RegisterObservationFailsClosedForAnUnknownPlanName()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("obs-unknown-plan.pst", "obs-unknown-plan@contoso.com");
        var fakeName = ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobName.Compute(
            scope.Tenant, scope.Project, wave.Id, 999);

        await Assert.ThrowsAsync<PurviewImportJobSourceNotFoundException>(() =>
            ObservationUseCase().ExecuteAsync(
                scope, wave.Id, fakeName, PurviewProviderOperationId.Create("purview-unknown"), PurviewImportJobObservedStatus.JobCreated,
                Clock.UtcNow, "operator@contoso.com", CancellationToken.None));
    }

    // ---- ImportPurviewServiceResultReportUseCase ----

    private static byte[] ReportBytes(string remotePstName, string status = "Succeeded", long? itemCount = 10) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            $"RemotePstName,Status,ImportedItemCount\n{remotePstName},{status}," +
            $"{(itemCount is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}\n");

    [Fact]
    public async Task ImportReportPersistsAndCorrelatesWithTheCanonicalPst()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("report-happy.pst", "report-happy@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;

        var evidence = await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, ReportBytes(remoteName), "operator", CancellationToken.None);

        Assert.Equal(1, evidence.ReportVersion);
        Assert.Equal(1, evidence.RowCount);
    }

    [Fact]
    public async Task ImportReportIsIdempotentForByteIdenticalContent()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("report-idempotent.pst", "report-idempotent@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;
        var bytes = ReportBytes(remoteName);
        var useCase = ImportReportUseCase();

        var first = await useCase.ExecuteAsync(scope, wave.Id, plan.PlannedJobName, bytes, "operator", CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, wave.Id, plan.PlannedJobName, bytes, "operator", CancellationToken.None);

        Assert.Equal(first.ReportVersion, second.ReportVersion);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_service_result_report_versions WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ImportReportProducesANewVersionForGenuinelyDifferentContent()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("report-newversion.pst", "report-newversion@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;
        var useCase = ImportReportUseCase();

        var first = await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, ReportBytes(remoteName, itemCount: 1), "operator", CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, ReportBytes(remoteName, itemCount: 2), "operator", CancellationToken.None);

        Assert.Equal(1, first.ReportVersion);
        Assert.Equal(2, second.ReportVersion);
    }

    [Fact]
    public async Task ImportReportFailsClosedWhenARowReferencesAPstOutsideTheCanonicalSet()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("report-unknown-pst.pst", "report-unknown-pst@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var unrelatedName = PurviewRemotePstName.ForPart(ArtifactId.New(), 1).Value;

        await Assert.ThrowsAsync<PurviewServiceResultCorrelationException>(() =>
            ImportReportUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, ReportBytes(unrelatedName), "operator", CancellationToken.None));
    }

    [Fact]
    public async Task ImportReportFailsClosedOnAMalformedReportWithoutPersistingAnything()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("report-malformed.pst", "report-malformed@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var malformed = new UTF8Encoding(false).GetBytes("NotEvenAHeaderThatIsRecognized\nsomevalue\n");

        await Assert.ThrowsAsync<PurviewServiceResultParsingException>(() =>
            ImportReportUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, malformed, "operator", CancellationToken.None));

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_service_result_report_versions WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ImportReportFailsClosedForAWaveOutsideTheCallersScope()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("report-idor.pst", "report-idor@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var otherScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;

        await Assert.ThrowsAsync<PurviewImportJobSourceNotFoundException>(() =>
            ImportReportUseCase().ExecuteAsync(otherScope, wave.Id, plan.PlannedJobName, ReportBytes(remoteName), "operator", CancellationToken.None));
    }

    [Fact]
    public async Task GetRowsFailsClosedWhenAPersistedRowIsTamperedDirectlyInSql()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("report-tampered.pst", "report-tampered@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;
        var evidence = await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plan.PlannedJobName, ReportBytes(remoteName), "operator", CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope,
            "UPDATE dbo.purview_service_result_rows SET imported_item_count = 999999 WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));

        await Assert.ThrowsAsync<PurviewServiceResultIntegrityViolationException>(() =>
            Reports().GetRowsAsync(scope, wave.Id, plan.PlannedJobName, evidence.ReportVersion, CancellationToken.None));
    }

    // ---- EvaluatePurviewServiceResultCompletenessUseCase ----

    [Fact]
    public async Task CompletenessIsIncompleteBeforeAnyReportIsImported()
    {
        var (scope, wave, _, _) = await SeedPlannableWaveAsync("completeness-none.pst", "completeness-none@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        var assessment = await CompletenessUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, CancellationToken.None);

        Assert.Equal(PurviewServiceResultCompletenessOutcome.Incomplete, assessment.Outcome);
    }

    [Fact]
    public async Task CompletenessIsCompleteForProviderEvidenceWhenTheReportCoversTheWholeCanonicalSetConclusively()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("completeness-full.pst", "completeness-full@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;
        await ImportReportUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, ReportBytes(remoteName), "operator", CancellationToken.None);

        var assessment = await CompletenessUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, CancellationToken.None);

        Assert.Equal(PurviewServiceResultCompletenessOutcome.CompleteForProviderEvidence, assessment.Outcome);
        Assert.Equal(1, assessment.CanonicalCount);
        Assert.Equal(1, assessment.MatchedCount);
    }

    [Fact]
    public async Task CompletenessIsInconclusiveWhenTheServiceNeverReportedAStatusForTheOnlyPst()
    {
        var (scope, wave, _, execution) = await SeedPlannableWaveAsync("completeness-unknown.pst", "completeness-unknown@contoso.com");
        var plan = await PlanUseCase().ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var remoteName = PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value;
        var noStatusReport = new UTF8Encoding(false).GetBytes($"RemotePstName\n{remoteName}\n");
        await ImportReportUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, noStatusReport, "operator", CancellationToken.None);

        var assessment = await CompletenessUseCase().ExecuteAsync(scope, wave.Id, plan.PlannedJobName, CancellationToken.None);

        Assert.Equal(PurviewServiceResultCompletenessOutcome.Inconclusive, assessment.Outcome);
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
