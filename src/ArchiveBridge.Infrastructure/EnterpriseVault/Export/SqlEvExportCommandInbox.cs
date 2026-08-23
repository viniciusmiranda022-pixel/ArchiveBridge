using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Fila durável de comandos de exportação EV sobre os Jobs do Slice 1, com FILTRO ESPECÍFICO por workload
/// <see cref="Workload.EnterpriseVault"/> — mesmo padrão de <c>SqlEvDiscoveryCommandInbox</c>. O
/// enfileiramento é SEMPRE idempotente pela identidade CANÔNICA do pedido (AB-4C-005 item 5): a chave é
/// procurada sob lock (<c>UPDLOCK, HOLDLOCK</c>) dentro do tenant/projeto ANTES de inserir; o índice único
/// filtrado é o backstop contra corrida.
/// </summary>
public sealed class SqlEvExportCommandInbox(TenantConnectionFactory connectionFactory, IClock clock) : IEvExportCommandInbox
{
    private const byte EvWorkload = (byte)Workload.EnterpriseVault;
    private const int UniqueViolation = 2601;
    private const int PrimaryKeyViolation = 2627;

    // Volume baixo: ordenação FIFO por criação (aging desprezível, janela ampla) — mesmo valor de SqlEvDiscoveryCommandInbox.
    private const long AgingSeconds = 315_360_000L;

    private const string LookupIdempotentSql =
        """
        SET NOCOUNT ON;
        SELECT request_id, job_id, connector_id, external_archive_id, max_threads, max_pst_size_mb, requested_by
        FROM dbo.ev_export_requests WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND canonical_idempotency_key = @key;
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

        INSERT INTO dbo.ev_export_requests
            (request_id, job_id, tenant_id, project_id, connector_id, external_archive_id, max_threads,
             max_pst_size_mb, requested_by, correlation_id, canonical_idempotency_key, created_at_utc)
        VALUES
            (@requestId, @jobId, @tenant, @projectId, @connector, @archiveId, @maxThreads, @maxPstSizeMb,
             @requestedBy, @correlation, @key, @now);
        """;

    private const string ClaimSql =
        """
        SET NOCOUNT ON;
        DECLARE @claimed TABLE (job_id UNIQUEIDENTIFIER, project_id UNIQUEIDENTIFIER, lease_epoch BIGINT,
                                lease_expires_at_utc DATETIME2(3), attempt_count INT, prior_state TINYINT);
        ;WITH candidate AS (
            SELECT TOP (1) j.job_id
            FROM dbo.jobs j WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE j.project_id = @project
              AND j.workload = @workload
              AND j.state IN (0, 2)
              AND (j.next_attempt_at_utc IS NULL OR j.next_attempt_at_utc <= @now)
              AND EXISTS (SELECT 1 FROM dbo.ev_export_requests er
                          WHERE er.job_id = j.job_id AND er.tenant_id = @tenant AND er.project_id = j.project_id)
            ORDER BY (CAST(j.priority AS BIGINT) + DATEDIFF_BIG(SECOND, j.created_at_utc, @now) / @agingSeconds) DESC,
                     j.created_at_utc ASC
        )
        UPDATE j
        SET state = 1,
            owner_worker = @worker,
            lease_epoch = j.lease_epoch + 1,
            lease_expires_at_utc = @leaseExpires,
            attempt_count = j.attempt_count + 1,
            next_attempt_at_utc = NULL,
            updated_at_utc = @now
        OUTPUT inserted.job_id, inserted.project_id, inserted.lease_epoch, inserted.lease_expires_at_utc,
               inserted.attempt_count, deleted.state
        INTO @claimed
        FROM dbo.jobs j INNER JOIN candidate c ON c.job_id = j.job_id;

        INSERT INTO dbo.job_attempts (job_id, tenant_id, project_id, attempt_number, owner_worker, lease_epoch, started_at_utc)
        SELECT c.job_id, @tenant, c.project_id, c.attempt_count, @worker, c.lease_epoch, @now FROM @claimed c;

        INSERT INTO dbo.job_state_transitions
            (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
        SELECT c.job_id, @tenant, c.project_id, c.prior_state, 1, 1, c.lease_epoch, @worker, @correlation, @now FROM @claimed c;

        SELECT c.job_id, c.lease_epoch, c.lease_expires_at_utc, c.attempt_count,
               er.request_id, er.connector_id, er.external_archive_id, er.max_threads, er.max_pst_size_mb,
               er.requested_by, er.correlation_id
        FROM @claimed c INNER JOIN dbo.ev_export_requests er ON er.job_id = c.job_id;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<EvExportEnqueueResult> EnqueueIdempotentAsync(
        EvExportCommand command, Guid canonicalIdempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (canonicalIdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A chave de idempotência canônica é obrigatória.", nameof(canonicalIdempotencyKey));
        }

        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);
        var scope = command.Scope;

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindByKeyAsync(connection.Connection, transaction, scope, canonicalIdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureSameCommand(existing.Value, command);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new EvExportEnqueueResult(new JobId(existing.Value.JobId), new ExportRequestId(existing.Value.RequestId), Created: false, Replayed: true);
            }

            var jobId = JobId.New();
            var requestId = ExportRequestId.New();
            try
            {
                await using var insert = new SqlCommand(EnqueueIdempotentSql, connection.Connection, transaction);
                BindEnqueue(insert, jobId, requestId, command, canonicalIdempotencyKey, now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException sql) when (sql.Number is UniqueViolation or PrimaryKeyViolation)
            {
                var raced = await FindByKeyAsync(connection.Connection, transaction, scope, canonicalIdempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                EnsureSameCommand(raced.Value, command);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new EvExportEnqueueResult(new JobId(raced.Value.JobId), new ExportRequestId(raced.Value.RequestId), Created: false, Replayed: true);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EvExportEnqueueResult(jobId, requestId, Created: true, Replayed: false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ClaimedEvExportCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var leaseExpires = SqlJobMapping.ToDbUtc(now + leaseDuration);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClaimedEvExportCommand? result = null;
            await using (var command = new SqlCommand(ClaimSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = EvWorkload });
                command.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = worker.Value });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
                command.Parameters.Add(new SqlParameter("@leaseExpires", SqlDbType.DateTime2) { Value = leaseExpires });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
                command.Parameters.Add(new SqlParameter("@agingSeconds", SqlDbType.BigInt) { Value = AgingSeconds });

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result = Read(reader, scope);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static ClaimedEvExportCommand Read(SqlDataReader reader, TenantScope scope)
    {
        var claimedJob = new ClaimedJob(
            new JobId(reader.GetGuid(0)),
            new LeaseEpoch(reader.GetInt64(1)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(2)),
            reader.GetInt32(3));

        var command = new EvExportCommand(
            scope,
            new ConnectorId(reader.GetGuid(5)),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetString(9),
            new CorrelationId(reader.GetGuid(10)));

        return new ClaimedEvExportCommand(claimedJob, new ExportRequestId(reader.GetGuid(4)), command);
    }

    private static void BindEnqueue(
        SqlCommand command, JobId jobId, ExportRequestId requestId, EvExportCommand source, Guid key, object now)
    {
        var scope = source.Scope;
        command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
        command.Parameters.Add(new SqlParameter("@requestId", SqlDbType.UniqueIdentifier) { Value = requestId.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = EvWorkload });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = source.Connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = source.ExternalArchiveId });
        command.Parameters.Add(new SqlParameter("@maxThreads", SqlDbType.Int) { Value = source.MaxThreads });
        command.Parameters.Add(new SqlParameter("@maxPstSizeMb", SqlDbType.Int) { Value = source.MaxPstSizeMb });
        command.Parameters.Add(new SqlParameter("@requestedBy", SqlDbType.NVarChar, 200) { Value = source.RequestedBy });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = source.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = key });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
    }

    private static async Task<ExistingRequest?> FindByKeyAsync(
        SqlConnection connection, SqlTransaction transaction, TenantScope scope, Guid key, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(LookupIdempotentSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = key });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExistingRequest(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6));
    }

    // A equivalência cobre TODO o alvo efetivamente persistido: connector, archive, MaxThreads,
    // MaxPstSizeMb. Não inclui correlation_id (muda por request) nem requested_by (é o solicitante, não o
    // alvo). Divergência ⇒ conflito determinístico — mas como a chave é DERIVADA desses mesmos campos
    // (EvExportRequestIdentity), uma divergência aqui só pode ocorrer por colisão de hash (praticamente
    // impossível) ou bug de chamador — nunca por uso legítimo normal.
    private static void EnsureSameCommand(ExistingRequest existing, EvExportCommand command)
    {
        var same = existing.ConnectorId == command.Connector.Value
            && string.Equals(existing.ExternalArchiveId, command.ExternalArchiveId, StringComparison.Ordinal)
            && existing.MaxThreads == command.MaxThreads
            && existing.MaxPstSizeMb == command.MaxPstSizeMb;
        if (!same)
        {
            throw new EvExportIdempotencyConflictException(
                "Chave de idempotência canônica já vinculada a um pedido de exportação de conteúdo divergente.");
        }
    }

    private readonly record struct ExistingRequest(
        Guid RequestId, Guid JobId, Guid ConnectorId, string ExternalArchiveId, int MaxThreads, int MaxPstSizeMb, string RequestedBy);
}
