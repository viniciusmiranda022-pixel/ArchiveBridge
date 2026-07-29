using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Jobs;

/// <summary>
/// Fila durável de Jobs sobre SQL Server. O claim é atômico (padrão READPAST + UPDLOCK: um único
/// vencedor), e complete/fail/retry são cercados por (owner_worker + lease_epoch). Toda mudança de
/// estado grava a auditoria na MESMA transação (nunca perdida). A RLS por SESSION_CONTEXT (definida
/// por <see cref="TenantConnectionFactory"/>) garante isolamento entre tenants.
/// </summary>
public sealed class SqlJobStore(TenantConnectionFactory connectionFactory, IClock clock) : IJobStore
{
    private const string ClaimSql =
        """
        SET NOCOUNT ON;
        DECLARE @claimed TABLE (job_id UNIQUEIDENTIFIER, project_id UNIQUEIDENTIFIER, lease_epoch BIGINT,
                                lease_expires_at_utc DATETIME2(3), attempt_count INT, prior_state TINYINT);
        ;WITH candidate AS (
            SELECT TOP (1) job_id
            FROM dbo.jobs WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE workload = @workload
              AND state IN (0, 2)
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
            ORDER BY priority DESC, next_attempt_at_utc ASC, created_at_utc ASC
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

        SELECT job_id, lease_epoch, lease_expires_at_utc, attempt_count FROM @claimed;
        """;

    private const string CreateSql =
        """
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE project_id = @projectId)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@projectId, @tenant, @now);

        INSERT INTO dbo.jobs
            (job_id, tenant_id, project_id, workload, state, priority, attempt_count, lease_epoch,
             next_attempt_at_utc, created_at_utc, updated_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, @workload, 0, @priority, 0, 0, @now, @now, @now);

        INSERT INTO dbo.job_state_transitions
            (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, NULL, 0, 0, 0, NULL, @correlation, @now);
        """;

    private const string TransitionSql =
        """
        SET NOCOUNT ON;
        DECLARE @applied TABLE (project_id UNIQUEIDENTIFIER, prior_state TINYINT);
        UPDATE dbo.jobs
        SET state = @toState,
            last_error_code = @lastError,
            next_attempt_at_utc = @nextAttempt,
            owner_worker = CASE WHEN @clearOwner = 1 THEN NULL ELSE owner_worker END,
            lease_expires_at_utc = NULL,
            updated_at_utc = @now
        OUTPUT inserted.project_id, deleted.state INTO @applied
        WHERE job_id = @jobId AND owner_worker = @worker AND lease_epoch = @epoch AND state = 1;

        IF EXISTS (SELECT 1 FROM @applied)
        BEGIN
            INSERT INTO dbo.job_state_transitions
                (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
            SELECT @jobId, @tenant, a.project_id, a.prior_state, @toState, @reason, @epoch, @worker, @correlation, @now
            FROM @applied a;
            SELECT 0 AS outcome;
        END
        ELSE
        BEGIN
            DECLARE @curState TINYINT, @curEpoch BIGINT;
            SELECT @curState = state, @curEpoch = lease_epoch FROM dbo.jobs WHERE job_id = @jobId;
            IF @curState IS NULL SELECT 3 AS outcome;
            ELSE IF @curEpoch = @epoch AND @curState = @toState SELECT 1 AS outcome;
            ELSE SELECT 2 AS outcome;
        END
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var jobId = JobId.New();
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var sqlCommand = new SqlCommand(CreateSql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = command.Scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = command.Scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = (byte)command.Workload });
                sqlCommand.Parameters.Add(new SqlParameter("@priority", SqlDbType.Int) { Value = command.Priority.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = command.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
                await sqlCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return jobId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock.UtcNow;
        var leaseExpires = SqlJobMapping.ToDbUtc(now + request.LeaseDuration);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(request.Scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClaimedJob? claimed = null;
            await using (var sqlCommand = new SqlCommand(ClaimSql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = (byte)request.Workload });
                sqlCommand.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = request.Worker.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = request.Scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = request.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@leaseExpires", SqlDbType.DateTime2) { Value = leaseExpires });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });

                await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    claimed = new ClaimedJob(
                        new JobId(reader.GetGuid(0)),
                        new LeaseEpoch(reader.GetInt64(1)),
                        SqlJobMapping.ReadUtc(reader.GetDateTime(2)),
                        reader.GetInt32(3));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken)
    {
        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var sqlCommand = new SqlCommand(
            $"SELECT {SqlJobMapping.JobColumns} FROM dbo.jobs WHERE job_id = @jobId;",
            tenantConnection.Connection);
        sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });

        await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return SqlJobMapping.ReadSnapshot(reader);
        }

        return null;
    }

    /// <inheritdoc />
    public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyTransitionAsync(command, toState: 3, reason: 2, lastError: null, nextAttempt: null, clearOwner: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyTransitionAsync(command, toState: 4, reason: 4, lastError: (byte)errorCode, nextAttempt: null, clearOwner: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobCommandOutcome> ScheduleRetryAsync(
        LeaseCommand command,
        ErrorCode errorCode,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyTransitionAsync(
            command,
            toState: 2,
            reason: 3,
            lastError: (byte)errorCode,
            nextAttempt: SqlJobMapping.ToDbUtc(nextAttemptAtUtc),
            clearOwner: true,
            cancellationToken);
    }

    private async Task<JobCommandOutcome> ApplyTransitionAsync(
        LeaseCommand command,
        byte toState,
        byte reason,
        byte? lastError,
        DateTime? nextAttempt,
        bool clearOwner,
        CancellationToken cancellationToken)
    {
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int outcome;
            await using (var sqlCommand = new SqlCommand(TransitionSql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@toState", SqlDbType.TinyInt) { Value = toState });
                sqlCommand.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = reason });
                sqlCommand.Parameters.Add(new SqlParameter("@lastError", SqlDbType.TinyInt) { Value = (object?)lastError ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@nextAttempt", SqlDbType.DateTime2) { Value = (object?)nextAttempt ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@clearOwner", SqlDbType.Bit) { Value = clearOwner });
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = command.JobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = command.Worker.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@epoch", SqlDbType.BigInt) { Value = command.Epoch.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = command.Scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = command.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });

                var scalar = await sqlCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                outcome = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (JobCommandOutcome)outcome;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
