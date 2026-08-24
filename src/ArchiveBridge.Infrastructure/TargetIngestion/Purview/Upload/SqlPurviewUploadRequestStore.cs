using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Fila durável do pedido lógico de upload Purview sobre os Jobs do Slice 1 (workload
/// <see cref="Workload.Upload"/>) — mesmo padrão de <c>SqlEvExportCommandInbox</c>. O enfileiramento é
/// SEMPRE idempotente por (tenant, projeto, wave): a chave é procurada sob lock
/// (<c>UPDLOCK, HOLDLOCK</c>) dentro do tenant/projeto ANTES de inserir; o índice único é o backstop
/// contra corrida. Um único pedido/Job por wave, para sempre (item 8/14).
/// </summary>
public sealed class SqlPurviewUploadRequestStore(TenantConnectionFactory connectionFactory, IClock clock) : IPurviewUploadRequestStore
{
    private const byte UploadWorkload = (byte)Workload.Upload;
    private const int UniqueViolation = 2601;
    private const int PrimaryKeyViolation = 2627;

    private const string Columns = "request_id, tenant_id, project_id, wave_id, job_id, correlation_id, created_at_utc, request_hash";

    private const string LookupByWaveSql =
        $"""
        SET NOCOUNT ON;
        SELECT {Columns}
        FROM dbo.purview_upload_requests WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND wave_id = @wave;
        """;

    private const string LookupCanonicalSql =
        $"""
        SET NOCOUNT ON;
        SELECT {Columns}
        FROM dbo.purview_upload_requests
        WHERE tenant_id = @tenant AND project_id = @project AND wave_id = @wave;
        """;

    private const string LookupByJobSql =
        $"""
        SET NOCOUNT ON;
        SELECT {Columns}
        FROM dbo.purview_upload_requests
        WHERE job_id = @job AND tenant_id = @tenant AND project_id = @project;
        """;

    private const string EnqueueIdempotentSql =
        """
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE project_id = @projectId)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@projectId, @tenant, @now);

        INSERT INTO dbo.jobs
            (job_id, tenant_id, project_id, workload, state, priority, attempt_count, lease_epoch,
             next_attempt_at_utc, created_at_utc, updated_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, @workload, 0, 0, 0, 0, @now, @now, @now);

        INSERT INTO dbo.job_state_transitions
            (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, NULL, 0, 0, 0, NULL, @correlation, @now);

        INSERT INTO dbo.purview_upload_requests (request_id, tenant_id, project_id, wave_id, job_id, correlation_id, created_at_utc, request_hash)
        VALUES (@requestId, @tenant, @projectId, @wave, @jobId, @correlation, @now, @requestHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<PurviewUploadRequestEnqueueResult> EnqueueIdempotentAsync(
        TenantScope scope, WaveId wave, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var nowDb = SqlJobMapping.ToDbUtc(now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindByWaveAsync(connection.Connection, transaction, scope, wave, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PurviewUploadRequestEnqueueResult(existing.Job, existing.Id, Created: false, Replayed: true);
            }

            var jobId = JobId.New();
            var requestId = PurviewUploadRequestId.New();
            var candidate = PurviewUploadRequest.Create(requestId, scope.Tenant, scope.Project, wave, jobId, correlation, now);

            try
            {
                await using var insert = new SqlCommand(EnqueueIdempotentSql, connection.Connection, transaction);
                BindEnqueue(insert, candidate, scope, wave, nowDb);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException sql) when (sql.Number is UniqueViolation or PrimaryKeyViolation)
            {
                var raced = await FindByWaveAsync(connection.Connection, transaction, scope, wave, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new PurviewUploadRequestEnqueueResult(raced.Job, raced.Id, Created: false, Replayed: true);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PurviewUploadRequestEnqueueResult(jobId, requestId, Created: true, Replayed: false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PurviewUploadRequest?> FindCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LookupCanonicalSql, connection.Connection);
        BindScopeAndWave(command, scope, wave);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRequest(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewUploadRequest?> GetByJobAsync(TenantScope scope, JobId job, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LookupByJobSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@job", SqlDbType.UniqueIdentifier) { Value = job.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRequest(reader) : null;
    }

    private static async Task<PurviewUploadRequest?> FindByWaveAsync(
        SqlConnection connection, SqlTransaction transaction, TenantScope scope, WaveId wave, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(LookupByWaveSql, connection, transaction);
        BindScopeAndWave(command, scope, wave);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRequest(reader) : null;
    }

    private static void BindScopeAndWave(SqlCommand command, TenantScope scope, WaveId wave)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
    }

    private static void BindEnqueue(SqlCommand command, PurviewUploadRequest candidate, TenantScope scope, WaveId wave, object now)
    {
        command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = candidate.Job.Value });
        command.Parameters.Add(new SqlParameter("@requestId", SqlDbType.UniqueIdentifier) { Value = candidate.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = UploadWorkload });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = candidate.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@requestHash", SqlDbType.Char, 64) { Value = candidate.RequestHash.Value });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
    }

    private static PurviewUploadRequest ReadRequest(SqlDataReader reader) => PurviewUploadRequest.Rehydrate(
        new PurviewUploadRequestId(reader.GetGuid(0)),
        new TenantId(reader.GetGuid(1)),
        new ProjectId(reader.GetGuid(2)),
        new WaveId(reader.GetGuid(3)),
        new JobId(reader.GetGuid(4)),
        new CorrelationId(reader.GetGuid(5)),
        SqlJobMapping.ReadUtc(reader.GetDateTime(6)),
        new Sha256Hash(reader.GetString(7).TrimEnd()));
}
