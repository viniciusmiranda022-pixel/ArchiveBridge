using System.Data;
using ArchiveBridge.Application.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I5-009 (SQL Server real) — pedido lógico durável de upload e a história append-only de tentativas:
/// enfileiramento idempotente (inclusive sob corrida real), isolamento cross-project (RLS), fencing da
/// gravação de tentativas sob o Job durável, e o CHECK que reforça evidência só quando Uploaded.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PurviewUploadIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();

    private SqlPurviewUploadRequestStore Requests() => new(fixture.Factory, Clock);

    private SqlPurviewUploadAttemptStore Attempts() => new(fixture.Factory);

    private SqlJobStore JobStore() => new(fixture.Factory, Clock, agingInterval: TimeSpan.FromSeconds(30));

    private async Task<(TenantScope Scope, MigrationWave Wave)> SeedApprovedWaveAsync(string name)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(
            scope, new WaveSelection([Slice2Support.Entry(name, "user@contoso.com", 4096)])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave);
    }

    [Fact]
    public async Task EnqueueIdempotentCreatesTheRequestAndItsJobAtomically()
    {
        var (scope, wave) = await SeedApprovedWaveAsync("upload-enqueue.pst");

        var result = await Requests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        Assert.True(result.Created);
        Assert.False(result.Replayed);

        var byJob = await Requests().GetByJobAsync(scope, result.JobId, CancellationToken.None);
        Assert.NotNull(byJob);
        Assert.Equal(result.RequestId, byJob!.Id);

        var canonical = await Requests().FindCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.NotNull(canonical);
        Assert.Equal(result.RequestId, canonical!.Id);
    }

    [Fact]
    public async Task ARepeatedEnqueueForTheSameWaveConvergesWithoutCreatingASecondJob()
    {
        var (scope, wave) = await SeedApprovedWaveAsync("upload-replay.pst");
        var store = Requests();

        var first = await store.EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        var second = await store.EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.True(second.Replayed);

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_upload_requests WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TwoConcurrentEnqueuesForTheSameWaveNeverProduceTwoRequests()
    {
        var (scope, wave) = await SeedApprovedWaveAsync("upload-race.pst");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => Requests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None)));

        Assert.Single(results.Select(result => result.RequestId).Distinct());
        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_upload_requests WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ARequestFromAnotherProjectIsIndistinguishableFromNotFound()
    {
        var (scope, wave) = await SeedApprovedWaveAsync("upload-cross-project.pst");
        await Requests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        var otherProjectScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        var fromOtherProject = await Requests().FindCanonicalAsync(otherProjectScope, wave.Id, CancellationToken.None);

        Assert.Null(fromOtherProject);
    }

    [Fact]
    public async Task AttemptAppendUnderAValidFenceSucceedsAndIsReadableAsTheLatest()
    {
        var (scope, wave) = await SeedApprovedWaveAsync("upload-attempt-happy.pst");
        var enqueue = await Requests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        var jobs = JobStore();
        var claimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new WorkerId("test-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);

        var fence = new JobFence(scope, claimed!.JobId, new WorkerId("test-worker"), claimed.Epoch);
        var now = Clock.UtcNow;
        var manifest = new[]
        {
            new PurviewUploadFileManifestItem(
                PartitionExecutionId.New(), PurviewRemotePstName.ForPart(ArtifactId.New(), 1), new Sha256Hash(new string('c', 64)), 4096),
            new PurviewUploadFileManifestItem(
                PartitionExecutionId.New(), PurviewRemotePstName.ForPart(ArtifactId.New(), 1), new Sha256Hash(new string('d', 64)), 4096),
        };
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id), manifest);
        var record = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, now, now);

        await Attempts().AppendAsync(scope, record, fence, CancellationToken.None);

        var latest = await Attempts().GetLatestAsync(scope, enqueue.RequestId, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(PurviewUploadAttemptOutcome.Uploaded, latest!.Outcome);
        Assert.NotNull(latest.Evidence);
        Assert.Equal(2, latest.Evidence!.ExpectedFileCount);
        Assert.Equal(evidence.ManifestHash, latest.Evidence.ManifestHash);
        Assert.Equal(2, latest.Evidence.Manifest.Count);
        Assert.All(manifest, item => Assert.Contains(latest.Evidence.Manifest, loaded => loaded.Execution == item.Execution));

        var all = await Attempts().ListAttemptsAsync(scope, enqueue.RequestId, CancellationToken.None);
        Assert.Single(all);
    }

    [Fact]
    public async Task AttemptAppendUnderALostFenceIsRejectedFailClosed()
    {
        var (scope, wave) = await SeedApprovedWaveAsync("upload-attempt-fenced.pst");
        var enqueue = await Requests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        var jobs = JobStore();
        var claimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new WorkerId("test-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);

        // Época STALE (fencing perdido) — simula um titular defasado tentando gravar após um Reclaim.
        var staleFence = new JobFence(scope, claimed!.JobId, new WorkerId("test-worker"), new LeaseEpoch(claimed.Epoch.Value + 1));
        var now = Clock.UtcNow;
        var record = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('c', 64)),
            PurviewUploadAttemptOutcome.ProcessFailed, "PROCESS_EXIT_NONZERO", Evidence: null, ProcessExitCode: 1, now, now);

        await Assert.ThrowsAsync<FencedOutException>(() => Attempts().AppendAsync(scope, record, staleFence, CancellationToken.None));

        var count = await CountAsync(
            scope, "SELECT COUNT(*) FROM dbo.purview_upload_attempts WHERE request_id = @request;", ("@request", enqueue.RequestId.Value));
        Assert.Equal(0, count);
    }

    // ---- AB-I5-015: manifestação por arquivo — persistência/rehydrate/tampering ----

    private async Task<(TenantScope Scope, PurviewUploadRequestId Request, JobFence Fence, PurviewUploadAttemptId Attempt)>
        AppendUploadedAttemptWithManifestAsync(string name, IReadOnlyList<PurviewUploadFileManifestItem> manifest)
    {
        var (scope, wave) = await SeedApprovedWaveAsync(name);
        var enqueue = await Requests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        var jobs = JobStore();
        var claimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new WorkerId("test-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);

        var fence = new JobFence(scope, claimed!.JobId, new WorkerId("test-worker"), claimed.Epoch);
        var now = Clock.UtcNow;
        var attemptId = PurviewUploadAttemptId.New();
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id), manifest);
        var record = new PurviewUploadAttemptRecord(
            enqueue.RequestId, attemptId, AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, now, now);
        await Attempts().AppendAsync(scope, record, fence, CancellationToken.None);

        return (scope, enqueue.RequestId, fence, attemptId);
    }

    [Fact]
    public async Task AttemptReadFailsClosedWhenAPersistedManifestItemHashIsTamperedDirectlyInTheRow()
    {
        var manifest = new[]
        {
            new PurviewUploadFileManifestItem(
                PartitionExecutionId.New(), PurviewRemotePstName.ForPart(ArtifactId.New(), 1), new Sha256Hash(new string('c', 64)), 4096),
        };
        var (scope, request, _, attempt) = await AppendUploadedAttemptWithManifestAsync("upload-manifest-tampered.pst", manifest);

        await ExecuteRawAsync(
            scope,
            "UPDATE dbo.purview_upload_attempt_manifest_items SET output_hash = REPLICATE('0', 64) WHERE attempt_id = @attempt;",
            ("@attempt", attempt.Value));

        await Assert.ThrowsAsync<PurviewUploadAttemptIntegrityViolationException>(() => Attempts().GetLatestAsync(scope, request, CancellationToken.None));
    }

    [Fact]
    public async Task AttemptReadFailsClosedWhenAnExtraManifestItemIsInsertedDirectlyInTheRow()
    {
        var manifest = new[]
        {
            new PurviewUploadFileManifestItem(
                PartitionExecutionId.New(), PurviewRemotePstName.ForPart(ArtifactId.New(), 1), new Sha256Hash(new string('c', 64)), 4096),
        };
        var (scope, request, _, attempt) = await AppendUploadedAttemptWithManifestAsync("upload-manifest-extra-item.pst", manifest);

        // Insere um SEGUNDO item de manifestação diretamente na tabela, sem passar pelo Domain/Application —
        // a mesma tentativa agora "cobre" um PST que NUNCA foi validado/incluído no manifest_hash gravado.
        await ExecuteRawAsync(
            scope,
            """
            INSERT INTO dbo.purview_upload_attempt_manifest_items
                (attempt_id, tenant_id, project_id, item_index, execution_id, remote_pst_name, output_hash, expected_size_bytes)
            VALUES (@attempt, @tenant, @project, 1, @execution, N'p_forged_part001.pst', REPLICATE('1', 64), 4096);
            """,
            ("@attempt", attempt.Value), ("@tenant", scope.Tenant.Value), ("@project", scope.Project.Value), ("@execution", Guid.NewGuid()));

        await Assert.ThrowsAsync<PurviewUploadAttemptIntegrityViolationException>(() => Attempts().GetLatestAsync(scope, request, CancellationToken.None));
    }

    private async Task ExecuteRawAsync(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
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
