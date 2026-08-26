using System.Data;
using System.Text;
using ArchiveBridge.Application.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.ControlPlane;
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
/// AB-I6-010 (SQL Server real) — <see cref="DisposeReconciliationExceptionUseCase"/>,
/// <see cref="GetReconciliationExceptionBacklogUseCase"/> e <see cref="SqlReconciliationExceptionDispositionStore"/>:
/// transições permitidas por resultado técnico, RBAC server-side, anti-IDOR, stale/superseded assessment,
/// idempotência/versionamento, concorrência (identica e conflitante) e tamper-evidence — sempre por cima do
/// resultado técnico já materializado pelo Passo 3 (AB-I6-007/0036), nunca alterando-o. STOP-THE-LINE:
/// nenhuma escrita EXO/Graph/Purview/EV, nenhuma conclusão de wave/projeto, nenhum certificate.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ReconciliationExceptionDispositionIntegrationTests(SqlServerFixture fixture)
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

    private SqlReconciliationExceptionDispositionStore Dispositions() => new(fixture.Factory);

    private FileSystemMappingArtifactStore Artifacts() =>
        new(Path.Combine(fixture.ArtifactRoot, "recon-disposition-" + Guid.NewGuid().ToString("N")));

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

    private DisposeReconciliationExceptionUseCase DisposeUseCase() => new(Assessments(), Dispositions(), Clock);

    private GetReconciliationExceptionBacklogUseCase BacklogUseCase() => new(Assessments(), Dispositions());

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
    /// import job criado — o piso completo para reconciliar (mesmo piso de <c>ReconciliationIntegrationTests</c>).
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
        var sb = new StringBuilder("RemotePstName,Status,ImportedItemCount,ImportedSizeBytes,SkippedItemCount,CorruptedItemCount\n");
        foreach (var (remoteName, status, importedItems, importedBytes) in rows)
        {
            sb.Append(remoteName).Append(',').Append(status).Append(',');
            sb.Append(importedItems?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            sb.Append(importedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(",0,0\n");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    /// <summary>Onda com UM PST cujo service result observado é "Failed" — item PST técnico Mismatch.</summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, ReconciliationAssessment Assessment, string RemoteName)>
        SeedMismatchAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(($"{caller}-mismatch.pst", $"{caller}-mismatch@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Failed", 0, 0)]), "operator", CancellationToken.None);
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id, plannedJobName, assessment, remoteName);
    }

    /// <summary>Onda com DOIS PSTs cujo relatório cobre só o primeiro — o segundo é IncompleteEvidence técnico.</summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, ReconciliationAssessment Assessment, string MissingRemoteName)>
        SeedIncompleteEvidenceAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(
            ($"{caller}-incomplete-a.pst", $"{caller}-incomplete-a@contoso.com"), ($"{caller}-incomplete-b.pst", $"{caller}-incomplete-b@contoso.com"));
        var coveredRemoteName = RemoteNameFor(entries[0].Execution);
        var missingRemoteName = RemoteNameFor(entries[1].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(coveredRemoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id, plannedJobName, assessment, missingRemoteName);
    }

    /// <summary>Onda com UM PST plenamente reconciliado — item PST técnico MatchedWithinEvidence (nunca uma exceção).</summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, ReconciliationAssessment Assessment, string RemoteName, MigrationWave WaveEntity)>
        SeedMatchedAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(($"{caller}-matched.pst", $"{caller}-matched@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        var archive = entries[0].Entry.Archive.Identity;
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(100, 10_000)))
            .ExecuteBeforeImportAsync(scope, wave.Id, archive, CorrelationId.New(), CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(110, 12_000)))
            .ExecuteAfterImportAsync(scope, wave.Id, archive, plannedJobName, CorrelationId.New(), CancellationToken.None);
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id, plannedJobName, assessment, remoteName, wave);
    }

    private static readonly ExoArchiveFolderStatisticObservation[] SingleFolder =
        [new("/Top of Information Store/Inbox", "Inbox", 10, 10, 2048, 2048, null, null)];

    private static ExoArchiveStatisticsObservation Observation(long? itemCount, long? totalSizeBytes) =>
        new(
            MailboxArchiveStatus.Active, Guid.NewGuid(), Guid.NewGuid(), itemCount, totalSizeBytes, TotalDeletedItemSizeBytes: 0,
            LastLogonTimeUtc: DateTimeOffset.UtcNow, RetentionHoldEnabled: false, LitigationHoldEnabled: false,
            AutoExpandingArchiveEnabled: false, Folders: SingleFolder, ObservedAtUtc: DateTimeOffset.UtcNow);

    private static DisposeReconciliationExceptionCommand Command(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        ReconciliationExceptionItemKind kind,
        string itemKey,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reason,
        int expectedVersion = 0,
        string? comment = null,
        string actor = "approver-1@contoso.com",
        string role = PortalRoles.Approver) =>
        new(scope, wave, plannedJobName, assessmentVersion, kind, itemKey, status, reason, expectedVersion, comment, actor, role, CorrelationId.New());

    // ---- 1-2: happy path sobre Mismatch ----

    [Fact]
    public async Task DisposeMovesAMismatchToRemediationRequired()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var decision = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None);

        Assert.Equal(ReconciliationExceptionDecisionStatus.RemediationRequired, decision.Status);
        Assert.Equal(ReconciliationDisposition.Mismatch, decision.TechnicalDisposition);
        Assert.Equal(1, decision.DecisionVersion);
    }

    [Fact]
    public async Task DisposeAcceptsAMismatchAsAnExceptionWithAValidReasonCode()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var decision = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
                comment: "Latência conhecida do provider; sem impacto material."),
            CancellationToken.None);

        Assert.Equal(ReconciliationExceptionDecisionStatus.AcceptedException, decision.Status);
    }

    // ---- 3: IncompleteEvidence -> AcceptedException somente por ação explícita autorizada ----

    [Fact]
    public async Task DisposeRejectsAcceptingIncompleteEvidenceWithoutTheAdministratorRole()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedIncompleteEvidenceAsync();

        await Assert.ThrowsAsync<ReconciliationExceptionAuthorizationException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException,
                ReconciliationExceptionReasonCode.IncompleteEvidenceAcceptedByExplicitOperationalPolicy, role: PortalRoles.Approver),
            CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAcceptsIncompleteEvidenceOnlyWithTheAdministratorRoleAndTheDedicatedReasonCode()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedIncompleteEvidenceAsync();

        var decision = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException,
                ReconciliationExceptionReasonCode.IncompleteEvidenceAcceptedByExplicitOperationalPolicy, role: PortalRoles.Administrator),
            CancellationToken.None);

        Assert.Equal(ReconciliationExceptionDecisionStatus.AcceptedException, decision.Status);
        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, decision.TechnicalDisposition);
    }

    // ---- 4: MatchedWithinEvidence nunca é uma exceção ----

    [Fact]
    public async Task DisposeRejectsAMatchedWithinEvidenceItem()
    {
        var (scope, wave, plannedJobName, assessment, remoteName, _) = await SeedMatchedAsync();

        await Assert.ThrowsAsync<ReconciliationExceptionNotDispositionableException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy),
            CancellationToken.None));
    }

    // Nota (item 5 dos testes obrigatórios): a rejeição de disposition sobre BlockedIntegrity é coberta em
    // ReconciliationExceptionDispositionDomainTests (EnsureDispositionableRejectsBlockedIntegrity) — o MESMO
    // padrão já usado por ReconciliationDomainTests para produzir BlockedIntegrity (feeding direto da
    // função pura de correlação): a segunda linha de defesa de ReconciliationArchiveCorrelation.IsCrossScope
    // nunca é alcançável através do pipeline real de captura/store (o store já filtra exatamente por
    // archive/fase — ver comentário em ReconciliationArchiveCorrelation), então NENHUM teste de integração
    // do Passo 3 (ReconciliationIntegrationTests) produz um item BlockedIntegrity real via SQL, e este Passo
    // segue o mesmo precedente.

    // ---- 6: RBAC nunca revela existência cross-scope ----

    [Fact]
    public async Task DisposeRejectsAnUnauthorizedRoleIdenticallyRegardlessOfWhetherTheItemExists()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var forMissingItem = await Assert.ThrowsAsync<ReconciliationExceptionAuthorizationException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, "does-not-exist.pst",
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired,
                role: PortalRoles.Operator),
            CancellationToken.None));

        var forRealItem = await Assert.ThrowsAsync<ReconciliationExceptionAuthorizationException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired,
                role: PortalRoles.Operator),
            CancellationToken.None));

        Assert.Equal(forMissingItem.Message, forRealItem.Message);
    }

    // ---- 7: anti-IDOR cross-tenant/project ----

    [Fact]
    public async Task DisposeFailsClosedWhenTheWaveDoesNotBelongToTheCallersScope()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();
        _ = scope;
        var otherScope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(otherScope), CorrelationId.New(), CancellationToken.None);

        await Assert.ThrowsAsync<PurviewImportJobSourceNotFoundException>(() => DisposeUseCase().ExecuteAsync(
            Command(otherScope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None));
    }

    // ---- 8: avaliação superseded bloqueia disposition antiga ----

    [Fact]
    public async Task DisposeFailsClosedWhenTheAssessmentWasSupersededSinceTheCallerObservedIt()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        // Mudança REAL de evidência observada (mesma técnica de ReconciliationIntegrationTests): uma nova
        // versão do relatório com contadores diferentes produz uma NOVA versão de avaliação.
        await ImportReportUseCase().ExecuteAsync(
            scope, wave, plannedJobName, ReportBytes([(remoteName, "Failed", 1, 1)]), "operator", CancellationToken.None);
        var superseding = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        Assert.True(superseding.AssessmentVersion > assessment.AssessmentVersion);

        await Assert.ThrowsAsync<ReconciliationExceptionStaleAssessmentException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None));

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_exception_dispositions WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(0, count);
    }

    // ---- 9-10: idempotência e versionamento explícito ----

    [Fact]
    public async Task DisposeConvergesIdempotentlyForAnIdenticalReplayOfTheSameDecision()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var first = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None);
        var replay = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None);

        Assert.Equal(first.DecisionVersion, replay.DecisionVersion);
        Assert.Equal(first.DecisionFingerprint, replay.DecisionFingerprint);
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_exception_dispositions WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DisposeCreatesANewExplicitVersionWhenTheDecisionGenuinelyChangesAndKeepsTheHistory()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var first = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None);
        var second = await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
                expectedVersion: first.DecisionVersion),
            CancellationToken.None);

        Assert.Equal(1, first.DecisionVersion);
        Assert.Equal(2, second.DecisionVersion);

        var history = await Dispositions().GetHistoryAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal(ReconciliationExceptionDecisionStatus.RemediationRequired, history[0].Status);
        Assert.Equal(ReconciliationExceptionDecisionStatus.AcceptedException, history[1].Status);
    }

    // ---- 11-12: concorrência ----

    [Fact]
    public async Task DisposeConvergesUnderFiveConcurrentIdenticalDecisionsInsteadOfDuplicating()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var tasks = Enumerable.Range(0, 5).Select(_ => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, decision => Assert.Equal(1, decision.DecisionVersion));
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_exception_dispositions WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(1, count);
    }

    private async Task<(ReconciliationExceptionDecision? Decision, Exception? Failure)> RunDisposeAsync(DisposeReconciliationExceptionCommand command)
    {
        try
        {
            var decision = await DisposeUseCase().ExecuteAsync(command, CancellationToken.None).ConfigureAwait(false);
            return (decision, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    [Fact]
    public async Task DisposeDetectsConflictingConcurrentDecisionsInsteadOfLastWriteWins()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        var commandA = Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
            ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired,
            actor: "approver-a@contoso.com");
        var commandB = Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
            ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
            actor: "approver-b@contoso.com");

        var results = await Task.WhenAll(RunDisposeAsync(commandA), RunDisposeAsync(commandB));

        // Exatamente uma das duas decisões conflitantes deve ter sucedido (versão 1); a outra é recusada com
        // ConcurrencyException — nunca ambas sucedendo silenciosamente (last-write-wins) nem ambas falhando.
        var successes = results.Where(result => result.Decision is not null).ToList();
        var failures = results.Where(result => result.Failure is not null).ToList();

        Assert.Single(successes);
        Assert.Single(failures);
        Assert.IsType<ConcurrencyException>(failures[0].Failure);
        Assert.Equal(1, successes[0].Decision!.DecisionVersion);

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_exception_dispositions WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(1, count);
    }

    // ---- 13: tampering direto no SQL ----

    [Fact]
    public async Task GetCurrentFailsClosedWhenTheReasonCodeIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedTamperableDecisionAsync();

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_exception_dispositions SET reason_code = 5 WHERE wave_id = @wave;", ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() => Dispositions().GetCurrentAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName, CancellationToken.None));
    }

    [Fact]
    public async Task GetCurrentFailsClosedWhenTheActorIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedTamperableDecisionAsync();

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_exception_dispositions SET decided_by = N'tampered-actor@evil.example' WHERE wave_id = @wave;",
            ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() => Dispositions().GetCurrentAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName, CancellationToken.None));
    }

    [Fact]
    public async Task GetCurrentFailsClosedWhenTheStatusIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedTamperableDecisionAsync();

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_exception_dispositions SET status = 1 WHERE wave_id = @wave;", ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() => Dispositions().GetCurrentAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName, CancellationToken.None));
    }

    [Fact]
    public async Task GetCurrentFailsClosedWhenTheDecidedAtTimestampIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedTamperableDecisionAsync();

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_exception_dispositions SET decided_at_utc = '2020-01-01T00:00:00.000' WHERE wave_id = @wave;",
            ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() => Dispositions().GetCurrentAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName, CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryFailsClosedWhenAnOlderVersionInTheHistoryIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedTamperableDecisionAsync();
        await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
                expectedVersion: 1),
            CancellationToken.None);

        // Adultera a versão MAIS ANTIGA (não a vigente) — o histórico completo precisa continuar
        // tamper-evident, não apenas a decisão vigente (item 7: "alteração posterior não pode apagar a
        // decisão anterior" — e, simetricamente, uma adulteração da anterior precisa ser detectável).
        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_exception_dispositions SET comment = N'forjado' WHERE wave_id = @wave AND decision_version = 1;",
            ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationIntegrityViolationException>(() => Dispositions().GetHistoryAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName, CancellationToken.None));
    }

    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, ReconciliationAssessment Assessment, string RemoteName)>
        SeedTamperableDecisionAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(($"{caller}-mismatch.pst", $"{caller}-mismatch@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Failed", 0, 0)]), "operator", CancellationToken.None);
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave.Id, plannedJobName, CorrelationId.New(), CancellationToken.None);
        await DisposeUseCase().ExecuteAsync(
            Command(scope, wave.Id, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None);
        return (scope, wave.Id, plannedJobName, assessment, remoteName);
    }

    // ---- 14: payload malformado recusado fail-closed de ponta a ponta ----

    [Fact]
    public async Task DisposeRejectsAMalformedItemKeyEndToEnd()
    {
        var (scope, wave, plannedJobName, assessment, _) = await SeedMismatchAsync();

        await Assert.ThrowsAsync<ReconciliationExceptionDispositionValidationException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, itemKey: "   ",
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None));
    }

    [Fact]
    public async Task DisposeRejectsACommentAboveTheLimitEndToEnd()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        await Assert.ThrowsAsync<ReconciliationExceptionDispositionValidationException>(() => DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired,
                comment: new string('x', 501)),
            CancellationToken.None));

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_exception_dispositions WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(0, count);
    }

    // ---- 15: STOP-THE-LINE ----

    [Fact]
    public async Task DisposeNeverChangesTheWaveStatus()
    {
        var (scope, wave, _, _, _, waveEntity) = await SeedMatchedAsync();
        var (scopeM, waveM, plannedJobNameM, assessmentM, remoteNameM) = await SeedMismatchAsync();

        await DisposeUseCase().ExecuteAsync(
            Command(scopeM, waveM, plannedJobNameM, assessmentM.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteNameM,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy),
            CancellationToken.None);

        var reread = await Slice2Support.WaveStore(fixture).GetAsync(scopeM, waveM, CancellationToken.None);
        Assert.NotNull(reread);

        // A onda usada apenas para produzir MatchedWithinEvidence permanece intocada também.
        var rereadMatched = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave, CancellationToken.None);
        Assert.Equal(waveEntity.Status, rereadMatched!.Status);
    }

    // ---- Read model de backlog (item 14) ----

    [Fact]
    public async Task BacklogStartsWithThePendingExceptionAndReflectsTheDecisionAfterward()
    {
        var (scope, wave, plannedJobName, assessment, remoteName) = await SeedMismatchAsync();

        // SeedMismatchAsync nunca captura snapshots EXO before/after do archive correlacionado — o item de
        // archive correspondente também entra no backlog como IncompleteEvidence pendente (item 11: qualquer
        // exceção técnica materializada, não somente a de PST). PendingCount cobre AMBAS.
        var before = await BacklogUseCase().ExecuteAsync(scope, wave, plannedJobName, CancellationToken.None);
        Assert.NotNull(before);
        Assert.Equal(2, before!.PendingCount);
        Assert.Equal(0, before.RemediationRequiredCount);

        await DisposeUseCase().ExecuteAsync(
            Command(scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired),
            CancellationToken.None);

        var after = await BacklogUseCase().ExecuteAsync(scope, wave, plannedJobName, CancellationToken.None);
        Assert.Equal(1, after!.PendingCount); // O item de archive IncompleteEvidence permanece pendente — só o de PST foi decidido.
        Assert.Equal(1, after.RemediationRequiredCount);
        var pstEntry = Assert.Single(after.Entries, entry => entry.ItemKind == ReconciliationExceptionItemKind.Pst);
        Assert.Equal(remoteName, pstEntry.ItemKey);
        Assert.Equal(ReconciliationExceptionDecisionStatus.RemediationRequired, pstEntry.CurrentStatus);
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
