using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Jobs;

/// <summary>
/// Gestão de leases sobre SQL Server: renovação (heartbeat) cercada por (owner_worker + lease_epoch)
/// e recuperação de leases expirados (reaper). O reaper usa uma conexão em modo de manutenção
/// (percorre todos os tenants de forma controlada) e devolve cada Job a RetryScheduled ou Failed,
/// conforme a política de retry — uma queda de worker nunca perde o Job.
/// </summary>
public sealed class SqlJobLeaseManager(
    TenantConnectionFactory connectionFactory,
    IClock clock,
    RetryPolicy retryPolicy,
    TimeSpan leaseDuration) : IJobLeaseManager
{
    private const string RenewSql =
        """
        SET NOCOUNT ON;
        UPDATE dbo.jobs
        SET lease_expires_at_utc = @leaseExpires, updated_at_utc = @now
        WHERE job_id = @jobId AND owner_worker = @worker AND lease_epoch = @epoch AND state = 1;
        IF @@ROWCOUNT > 0 SELECT 0 AS outcome;
        ELSE IF EXISTS (SELECT 1 FROM dbo.jobs WHERE job_id = @jobId) SELECT 2 AS outcome;
        ELSE SELECT 3 AS outcome;
        """;

    private const string SelectExpiredSql =
        """
        SELECT TOP (@batchSize) job_id, tenant_id, project_id, attempt_count, lease_epoch, owner_worker
        FROM dbo.jobs
        WHERE state = 1 AND lease_expires_at_utc IS NOT NULL AND lease_expires_at_utc < @now
        ORDER BY lease_expires_at_utc ASC;
        """;

    private const string RecoverOneSql =
        """
        SET NOCOUNT ON;
        DECLARE @applied TABLE (project_id UNIQUEIDENTIFIER);
        UPDATE dbo.jobs
        SET state = @toState,
            next_attempt_at_utc = @nextAttempt,
            lease_expires_at_utc = NULL,
            owner_worker = NULL,
            last_error_code = @lastError,
            updated_at_utc = @now
        OUTPUT inserted.project_id INTO @applied
        WHERE job_id = @jobId AND lease_epoch = @epoch AND state = 1;

        IF EXISTS (SELECT 1 FROM @applied)
        BEGIN
            INSERT INTO dbo.job_state_transitions
                (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
            SELECT @jobId, @tenant, a.project_id, 1, @toState, @reason, @epoch, @worker, @correlation, @now FROM @applied a;
            SELECT 1 AS recovered;
        END
        ELSE SELECT 0 AS recovered;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;
    private readonly RetryPolicy _retryPolicy = retryPolicy;
    private readonly TimeSpan _leaseDuration = leaseDuration;

    /// <inheritdoc />
    public async Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = _clock.UtcNow;

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        await using var sqlCommand = new SqlCommand(RenewSql, tenantConnection.Connection);
        sqlCommand.Parameters.Add(new SqlParameter("@leaseExpires", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now + _leaseDuration) });
        sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
        sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = command.JobId.Value });
        sqlCommand.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = command.Worker.Value });
        sqlCommand.Parameters.Add(new SqlParameter("@epoch", SqlDbType.BigInt) { Value = command.Epoch.Value });

        var scalar = await sqlCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (JobCommandOutcome)Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var candidates = new List<ExpiredLease>();

        await using var maintenanceConnection = await _connectionFactory
            .OpenForMaintenanceAsync(cancellationToken).ConfigureAwait(false);

        await using (var select = new SqlCommand(SelectExpiredSql, maintenanceConnection.Connection))
        {
            select.Parameters.Add(new SqlParameter("@batchSize", SqlDbType.Int) { Value = batchSize });
            select.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new ExpiredLease(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var recovered = 0;
        foreach (var candidate in candidates)
        {
            if (await RecoverOneAsync(maintenanceConnection.Connection, candidate, now, cancellationToken).ConfigureAwait(false))
            {
                recovered++;
            }
        }

        return recovered;
    }

    private async Task<bool> RecoverOneAsync(
        SqlConnection connection,
        ExpiredLease candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retry = _retryPolicy.ShouldRetry(candidate.AttemptCount);
        var toState = retry ? (byte)2 : (byte)4;                 // RetryScheduled : Failed
        var reason = retry ? (byte)6 : (byte)7;                  // LeaseExpiredRecovered : AttemptsExhausted
        object nextAttempt = retry
            ? SqlJobMapping.ToDbUtc(_retryPolicy.NextAttempt(candidate.AttemptCount, now))
            : DBNull.Value;
        object lastError = retry ? DBNull.Value : (byte)ErrorCode.ResourceExhaustion;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int result;
            await using (var command = new SqlCommand(RecoverOneSql, connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@toState", SqlDbType.TinyInt) { Value = toState });
                command.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = reason });
                command.Parameters.Add(new SqlParameter("@nextAttempt", SqlDbType.DateTime2) { Value = nextAttempt });
                command.Parameters.Add(new SqlParameter("@lastError", SqlDbType.TinyInt) { Value = lastError });
                command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = candidate.JobId });
                command.Parameters.Add(new SqlParameter("@epoch", SqlDbType.BigInt) { Value = candidate.LeaseEpoch });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = candidate.TenantId });
                command.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = (object?)candidate.OwnerWorker ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = CorrelationId.New().Value });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                result = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result == 1;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record ExpiredLease(Guid JobId, Guid TenantId, int AttemptCount, long LeaseEpoch, string? OwnerWorker);
}
