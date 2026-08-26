using System.Data;
using System.Text;
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
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Reconciliation;
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
/// AB-I6-013 (SQL Server real) — <see cref="IssueReconciliationCertificateUseCase"/>,
/// <see cref="GetReconciliationCertificateUseCase"/>, <see cref="VerifyReconciliationCertificateUseCase"/> e
/// <see cref="SqlReconciliationCertificateStore"/>: resultado canônico sempre em cima da cadeia já
/// materializada pelos Passos 1-4, 100% evidence completeness, RBAC server-side, anti-IDOR,
/// idempotência/versionamento, staleness/conflito de evidência sob emissão, tamper-evidence e
/// supersession — NUNCA marca wave/projeto COMPLETED, NUNCA é sign-off final, NUNCA escreve em
/// EXO/Graph/Purview/EV (STOP-THE-LINE).
/// <para>
/// <c>BlockedIntegrity</c> e a detecção cross-attempt de <c>DUPLICATE_RISK</c> não são reprodutíveis via o
/// pipeline real deste fixture pelos MESMOS motivos documentados em
/// <see cref="ReconciliationExceptionDispositionIntegrationTests"/> (BlockedIntegrity nunca é produzido
/// pelo store real de correlação) e porque uma segunda tentativa (attempt) genuína da MESMA onda exigiria
/// reabrir/replanejar vínculos fora do escopo deste Passo — ambos os precedentes de resultado são cobertos
/// exaustivamente por <c>ReconciliationCertificateDomainTests</c> (precedência pura, sem I/O).
/// </para>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ReconciliationCertificateIntegrationTests(SqlServerFixture fixture)
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

    private SqlReconciliationCertificateStore Certificates() => new(fixture.Factory);

    private FileSystemMappingArtifactStore Artifacts() =>
        new(Path.Combine(fixture.ArtifactRoot, "recon-certificate-" + Guid.NewGuid().ToString("N")));

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

    private static readonly IAuthenticatedActorAccessor DefaultAdministratorActor =
        new FakeAuthenticatedActorAccessor("admin-1@contoso.com", PortalRoles.Administrator);

    private DisposeReconciliationExceptionUseCase DisposeUseCase(IAuthenticatedActorAccessor? actorAccessor = null) =>
        new(Assessments(), Dispositions(), Clock, actorAccessor ?? DefaultAdministratorActor);

    private IssueReconciliationCertificateUseCase IssueUseCase(IAuthenticatedActorAccessor? actorAccessor = null) =>
        new(ReconciliationUseCase(), EvidenceResolver(), MappingStore(), Assessments(), Dispositions(), Certificates(), Clock, actorAccessor ?? DefaultAdministratorActor);

    private GetReconciliationCertificateUseCase GetUseCase(IAuthenticatedActorAccessor? actorAccessor = null) =>
        new(Certificates(), Assessments(), Dispositions(), actorAccessor ?? DefaultAdministratorActor, Clock);

    private VerifyReconciliationCertificateUseCase VerifyUseCase(IAuthenticatedActorAccessor? actorAccessor = null) =>
        new(Certificates(), actorAccessor ?? DefaultAdministratorActor, Clock);

    /// <summary>Ator autenticado de teste — mesmo padrão de <see cref="ReconciliationExceptionDispositionIntegrationTests"/>.</summary>
    private sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current { get; } = new(actorId, roles);
    }

    private sealed class UnauthenticatedActorAccessor : IAuthenticatedActorAccessor
    {
        public AuthenticatedActor Current => throw new InvalidOperationException(
            "Nenhum principal autenticado no contexto atual (fail-closed).");
    }

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

    private static readonly ExoArchiveFolderStatisticObservation[] SingleFolder =
        [new("/Top of Information Store/Inbox", "Inbox", 10, 10, 2048, 2048, null, null)];

    private static ExoArchiveStatisticsObservation Observation(long? itemCount, long? totalSizeBytes) =>
        new(
            MailboxArchiveStatus.Active, Guid.NewGuid(), Guid.NewGuid(), itemCount, totalSizeBytes, TotalDeletedItemSizeBytes: 0,
            LastLogonTimeUtc: DateTimeOffset.UtcNow, RetentionHoldEnabled: false, LitigationHoldEnabled: false,
            AutoExpandingArchiveEnabled: false, Folders: SingleFolder, ObservedAtUtc: DateTimeOffset.UtcNow);

    /// <summary>Onda com UM PST plenamente reconciliado — evidência 100% completa, zero exceções materiais (happy PASS).</summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, MigrationWave WaveEntity)>
        SeedFullyMatchedAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
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
        return (scope, wave.Id, plannedJobName, wave);
    }

    /// <summary>
    /// Onda com UM PST cujo service result observado é "Failed" — item PST técnico Mismatch (nenhuma
    /// disposition ainda). Captura snapshots EXO before/after do archive correlacionado (ao contrário do
    /// equivalente em <see cref="ReconciliationExceptionDispositionIntegrationTests"/>) para que o item de
    /// archive resolva como <see cref="ReconciliationDisposition.MatchedWithinEvidence"/> — isolando o PST
    /// Mismatch como a ÚNICA exceção material da avaliação, necessário para os testes de certificate que
    /// verificam o resultado canônico produzido especificamente por essa exceção (evidência 100% completa).
    /// </summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, string RemoteName)>
        SeedMismatchAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(($"{caller}-mismatch.pst", $"{caller}-mismatch@contoso.com"));
        var remoteName = RemoteNameFor(entries[0].Execution);
        var archive = entries[0].Entry.Archive.Identity;
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(remoteName, "Failed", 0, 0)]), "operator", CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(100, 10_000)))
            .ExecuteBeforeImportAsync(scope, wave.Id, archive, CorrelationId.New(), CancellationToken.None);
        await CaptureUseCase(new FakeExoArchiveStatisticsAdapter(Observation(110, 12_000)))
            .ExecuteAfterImportAsync(scope, wave.Id, archive, plannedJobName, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id, plannedJobName, remoteName);
    }

    /// <summary>Onda com DOIS PSTs cujo relatório cobre só o primeiro — o segundo é IncompleteEvidence técnico.</summary>
    private async Task<(TenantScope Scope, WaveId Wave, PurviewImportJobName PlannedJobName, string MissingRemoteName)>
        SeedIncompleteEvidenceAsync([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var (scope, wave, entries, plannedJobName) = await SeedPlannedWaveAsync(
            ($"{caller}-incomplete-a.pst", $"{caller}-incomplete-a@contoso.com"), ($"{caller}-incomplete-b.pst", $"{caller}-incomplete-b@contoso.com"));
        var coveredRemoteName = RemoteNameFor(entries[0].Execution);
        var missingRemoteName = RemoteNameFor(entries[1].Execution);
        await ImportReportUseCase().ExecuteAsync(
            scope, wave.Id, plannedJobName, ReportBytes([(coveredRemoteName, "Succeeded", 10, 2048)]), "operator", CancellationToken.None);
        return (scope, wave.Id, plannedJobName, missingRemoteName);
    }

    private static IssueReconciliationCertificateCommand Command(TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName) =>
        new(scope, wave, plannedJobName, CorrelationId.New());

    // ---- 1: happy path PASS ----

    [Fact]
    public async Task IssueEmitsPassWhenEvidenceIsCompleteAndThereAreNoMaterialExceptions()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();

        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(ReconciliationOutcome.Pass, certificate.Result);
        Assert.True(certificate.Completeness.IsComplete);
        Assert.Equal(0, certificate.DeviationCount);
        Assert.False(certificate.DuplicateRiskDetected);
        Assert.Equal(1, certificate.CertificateVersion);
    }

    // ---- 2: PASS_WITH_EXPLAINED_EXCEPTIONS somente com disposition vigente aceita ----

    [Fact]
    public async Task IssueEmitsPassWithExplainedExceptionsWhenTheOnlyMaterialExceptionHasAnAcceptedDisposition()
    {
        var (scope, wave, plannedJobName, remoteName) = await SeedMismatchAsync();
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        await DisposeUseCase().ExecuteAsync(
            new DisposeReconciliationExceptionCommand(
                scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
                ExpectedCurrentDecisionVersion: 0, Comment: null, CorrelationId.New()),
            CancellationToken.None);

        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(ReconciliationOutcome.PassWithExplainedExceptions, certificate.Result);
        Assert.True(certificate.DeviationCount > 0);
    }

    // ---- 3/6: RemediationRequired/Pending nunca produz sucesso ----

    [Fact]
    public async Task IssueEmitsFailWhenAMaterialExceptionIsRemediationRequired()
    {
        var (scope, wave, plannedJobName, remoteName) = await SeedMismatchAsync();
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        await DisposeUseCase().ExecuteAsync(
            new DisposeReconciliationExceptionCommand(
                scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, remoteName,
                ReconciliationExceptionDecisionStatus.RemediationRequired, ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired,
                ExpectedCurrentDecisionVersion: 0, Comment: null, CorrelationId.New()),
            CancellationToken.None);

        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(ReconciliationOutcome.Fail, certificate.Result);
    }

    [Fact]
    public async Task IssueEmitsFailWhenAMaterialExceptionHasNoDispositionAtAll()
    {
        var (scope, wave, plannedJobName, _) = await SeedMismatchAsync();

        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(ReconciliationOutcome.Fail, certificate.Result);
    }

    // ---- 4/7: evidência incompleta nunca vira sucesso, mesmo com disposition aceita sobre ela ----

    [Fact]
    public async Task IssueEmitsInconclusiveWhenEvidenceIsIncomplete()
    {
        var (scope, wave, plannedJobName, _) = await SeedIncompleteEvidenceAsync();

        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(ReconciliationOutcome.Inconclusive, certificate.Result);
        Assert.False(certificate.Completeness.IsComplete);
    }

    [Fact]
    public async Task IssueRemainsInconclusiveEvenWhenTheIncompleteEvidenceItemIsAcceptedByAnAdministrator()
    {
        // Item 4/36: aceitar o RISCO OPERACIONAL (AcceptedException) de IncompleteEvidence nunca torna a
        // EVIDÊNCIA completa — os dois conceitos são deliberadamente independentes.
        var (scope, wave, plannedJobName, missingRemoteName) = await SeedIncompleteEvidenceAsync();
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        await DisposeUseCase().ExecuteAsync(
            new DisposeReconciliationExceptionCommand(
                scope, wave, plannedJobName, assessment.AssessmentVersion, ReconciliationExceptionItemKind.Pst, missingRemoteName,
                ReconciliationExceptionDecisionStatus.AcceptedException, ReconciliationExceptionReasonCode.IncompleteEvidenceAcceptedByExplicitOperationalPolicy,
                ExpectedCurrentDecisionVersion: 0, Comment: null, CorrelationId.New()),
            CancellationToken.None);

        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(ReconciliationOutcome.Inconclusive, certificate.Result);
    }

    // ---- 9: staleness/conflito de evidência durante a emissão (nível de store) ----

    [Fact]
    public async Task IssueOrConvergeFailsClosedWhenTheAssessmentVersionIsNoLongerCurrent()
    {
        var (scope, wave, plannedJobName, remoteName) = await SeedMismatchAsync();
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);

        // Evidência muda de verdade — nova versão de avaliação supersede a anterior.
        await ImportReportUseCase().ExecuteAsync(
            scope, wave, plannedJobName, ReportBytes([(remoteName, "Failed", 1, 1)]), "operator", CancellationToken.None);
        var superseding = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        Assert.True(superseding.AssessmentVersion > assessment.AssessmentVersion);

        var emptyDecisionsFingerprint = ReconciliationExceptionDecisionsStateHash.Compute([]);
        await Assert.ThrowsAsync<ReconciliationCertificateStaleChainException>(() => Certificates().IssueOrConvergeAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, assessment.SourceFingerprint, new Sha256Hash(new string('a', 64)),
            emptyDecisionsFingerprint, ReconciliationOutcome.Fail, totalItemCount: 1, incompleteItemCount: 0, deviationCount: 1,
            new Sha256Hash(new string('b', 64)), duplicateRiskDetected: false, "admin@contoso.com", PortalRoles.Administrator,
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None));

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_certificates WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task IssueOrConvergeFailsClosedWhenTheDecisionsStateFingerprintDivergesFromWhatWasActuallyLocked()
    {
        var (scope, wave, plannedJobName, _) = await SeedMismatchAsync();
        var assessment = await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);

        // Nenhuma decisão foi registrada — o estado REAL vigente é a lista vazia; um fingerprint "esperado"
        // que finge uma decisão inexistente nunca pode corresponder ao que a store realmente locka.
        var forgedDecisionsFingerprint = new Sha256Hash(new string('f', 64));

        await Assert.ThrowsAsync<ReconciliationCertificateStaleChainException>(() => Certificates().IssueOrConvergeAsync(
            scope, wave, plannedJobName, assessment.AssessmentVersion, assessment.SourceFingerprint, new Sha256Hash(new string('a', 64)),
            forgedDecisionsFingerprint, ReconciliationOutcome.Fail, totalItemCount: 1, incompleteItemCount: 0, deviationCount: 1,
            new Sha256Hash(new string('b', 64)), duplicateRiskDetected: false, "admin@contoso.com", PortalRoles.Administrator,
            CorrelationId.New(), Clock.UtcNow, CancellationToken.None));
    }

    // ---- 10: replay idempotente ----

    [Fact]
    public async Task IssueConvergesIdempotentlyForAnIdenticalReplay()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();

        var first = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);
        var replay = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal(first.CertificateVersion, replay.CertificateVersion);
        Assert.Equal(first.CertificateHash, replay.CertificateHash);
        Assert.Equal(first.EvaluationFingerprint, replay.EvaluationFingerprint);

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_certificates WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(1, count);
    }

    // ---- 11: 5+ emissões concorrentes idênticas convergem para um único certificate ----

    [Fact]
    public async Task IssueConvergesUnderFiveConcurrentIdenticalIssuancesInsteadOfDuplicating()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();

        var tasks = Enumerable.Range(0, 5).Select(_ => IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, certificate => Assert.Equal(1, certificate.CertificateVersion));
        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_certificates WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(1, count);
    }

    // ---- 13: tampering direto no SQL ----

    [Fact]
    public async Task GetLatestFailsClosedWhenTheResultIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_certificates SET result = 4 WHERE wave_id = @wave;", ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationCertificateIntegrityViolationException>(
            () => Certificates().GetLatestAsync(scope, wave, plannedJobName, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestFailsClosedWhenTheIssuedByColumnIsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_certificates SET issued_by = N'tampered-actor@evil.example' WHERE wave_id = @wave;",
            ("@wave", wave.Value));

        await Assert.ThrowsAsync<ReconciliationCertificateIntegrityViolationException>(
            () => Certificates().GetLatestAsync(scope, wave, plannedJobName, CancellationToken.None));
    }

    [Fact]
    public async Task GetByVersionFailsClosedWhenTheDeviationsSha256IsTamperedDirectlyInSql()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        var certificate = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        await ExecuteAdminSqlAsync(
            scope, "UPDATE dbo.purview_reconciliation_certificates SET deviations_sha256 = @forged WHERE wave_id = @wave;",
            ("@wave", wave.Value), ("@forged", new string('9', 64)));

        await Assert.ThrowsAsync<ReconciliationCertificateIntegrityViolationException>(
            () => Certificates().GetByVersionAsync(scope, wave, plannedJobName, certificate.CertificateVersion, CancellationToken.None));
    }

    // ---- 15: anti-IDOR cross-tenant/project ----

    [Fact]
    public async Task IssueFailsClosedWhenTheWaveDoesNotBelongToTheCallersScope()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        _ = scope;
        var otherScope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(otherScope), CorrelationId.New(), CancellationToken.None);

        await Assert.ThrowsAsync<PurviewImportJobSourceNotFoundException>(
            () => IssueUseCase().ExecuteAsync(Command(otherScope, wave, plannedJobName), CancellationToken.None));
    }

    // ---- 16: RBAC — papel não autorizado, e ator/papel nunca vêm do payload ----

    [Fact]
    public async Task IssueRejectsAnUnauthorizedRole()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        var viewer = new FakeAuthenticatedActorAccessor("viewer-1@contoso.com", PortalRoles.Viewer);

        await Assert.ThrowsAsync<ReconciliationCertificateAuthorizationException>(
            () => IssueUseCase(viewer).ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None));

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_certificates WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task IssueFailsClosedBeforeAnyScopedReadWhenThereIsNoAuthenticatedPrincipal()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => IssueUseCase(new UnauthenticatedActorAccessor()).ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None));

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.purview_reconciliation_certificates WHERE wave_id = @wave;", ("@wave", wave.Value));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task IssuePersistsTheServerSidePrincipalAsIssuedByNeverAValueFromTheCommand()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        var administrator = new FakeAuthenticatedActorAccessor("server-side-admin@contoso.com", PortalRoles.Administrator);

        var certificate = await IssueUseCase(administrator).ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        Assert.Equal("server-side-admin@contoso.com", certificate.IssuedBy);
        Assert.Equal(PortalRoles.Administrator, certificate.IssuedByRole);
    }

    // ---- 18: supersession — certificate antigo permanece histórico, mas marcado stale na leitura ----

    [Fact]
    public async Task GetIdentifiesAPreviouslyCurrentCertificateAsSupersededAfterNewCanonicalEvidenceWithoutDeletingIt()
    {
        var (scope, wave, plannedJobName, remoteName) = await SeedMismatchAsync();
        var first = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);
        Assert.Equal(ReconciliationOutcome.Fail, first.Result);

        var beforeNewEvidence = await GetUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(beforeNewEvidence);
        Assert.False(beforeNewEvidence!.IsSuperseded);

        // Evidência canônica avança: novo relatório -> nova versão de avaliação.
        await ImportReportUseCase().ExecuteAsync(
            scope, wave, plannedJobName, ReportBytes([(remoteName, "Failed", 1, 1)]), "operator", CancellationToken.None);
        await ReconciliationUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);

        var afterNewEvidence = await GetUseCase().ExecuteAsync(scope, wave, plannedJobName, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(afterNewEvidence);
        Assert.True(afterNewEvidence!.IsSuperseded);
        Assert.Equal(first.CertificateVersion, afterNewEvidence.Certificate.CertificateVersion); // histórico preservado, nunca apagado

        var history = await Certificates().GetHistoryAsync(scope, wave, plannedJobName, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal(first.CertificateHash, history[0].CertificateHash);
    }

    // ---- 19: STOP-THE-LINE — a emissão nunca altera o status da wave ----

    [Fact]
    public async Task IssueNeverChangesTheWaveStatus()
    {
        var (scope, wave, plannedJobName, waveEntity) = await SeedFullyMatchedAsync();

        await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        var reread = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal(waveEntity.Status, reread!.Status);
    }

    // ---- Verify explícito (item 14) ----

    [Fact]
    public async Task VerifyReturnsTheSameCertificateWhenIntegrityIsIntact()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        var issued = await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        var verified = await VerifyUseCase().ExecuteAsync(scope, wave, plannedJobName, issued.CertificateVersion, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(verified);
        Assert.Equal(issued.CertificateHash, verified!.CertificateHash);
    }

    [Fact]
    public async Task VerifyThrowsAndReturnsNullForAnUnknownVersion()
    {
        var (scope, wave, plannedJobName, _) = await SeedFullyMatchedAsync();
        await IssueUseCase().ExecuteAsync(Command(scope, wave, plannedJobName), CancellationToken.None);

        var result = await VerifyUseCase().ExecuteAsync(scope, wave, plannedJobName, certificateVersion: 999, CorrelationId.New(), CancellationToken.None);

        Assert.Null(result);
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
