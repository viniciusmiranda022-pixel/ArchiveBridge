using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Fila durável de comandos de descoberta EV sobre os Jobs do Slice 1, com FILTRO ESPECÍFICO por workload
/// <see cref="Workload.EnterpriseVault"/> (nunca Control genérico). O enfileiramento é ATÔMICO: numa única
/// transação cria o Job EnterpriseVault (Pending) + a transição inicial + o contexto do comando em
/// <c>ev_discovery_commands</c>. A reivindicação é EXCLUSIVA: o claim atômico (READPAST/UPDLOCK, fencing
/// por época) só considera Jobs EnterpriseVault que POSSUAM contexto (via <c>EXISTS</c>) e o carrega na
/// MESMA operação — um Job sem contexto não é reivindicado.
/// </summary>
public sealed class SqlEvDiscoveryCommandInbox(TenantConnectionFactory connectionFactory, IClock clock)
    : IEvDiscoveryCommandInbox
{
    private const byte EvWorkload = (byte)Workload.EnterpriseVault;

    // Descoberta é de baixo volume: ordenação FIFO por criação (aging desprezível, janela ampla).
    private const long AgingSeconds = 315_360_000L;

    private const string EnqueueSql =
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

        INSERT INTO dbo.ev_discovery_commands
            (job_id, tenant_id, project_id, environment_id, site_name, directory_server, requested_by,
             command_schema_version, expected_project_configuration_version, expected_configuration_hash,
             discovery_policy_version, correlation_id, created_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, @environment, @site, @directory, @requestedBy,
             @schemaVersion, @expProjectCfgVer, @expCfgHash, @policyVersion, @correlation, @now);
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
              AND EXISTS (SELECT 1 FROM dbo.ev_discovery_commands dc
                          WHERE dc.job_id = j.job_id AND dc.tenant_id = @tenant AND dc.project_id = j.project_id)
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
               dc.environment_id, dc.site_name, dc.directory_server, dc.requested_by,
               dc.command_schema_version, dc.expected_project_configuration_version, dc.expected_configuration_hash,
               dc.discovery_policy_version, dc.correlation_id
        FROM @claimed c INNER JOIN dbo.ev_discovery_commands dc ON dc.job_id = c.job_id;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<JobId> EnqueueAsync(EvDiscoveryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Fail-closed: contexto obrigatório validado ANTES de enfileirar (espelha os NOT NULL/CHECK no banco).
        EvDiscoveryCommandValidation.EnsureValid(command);
        var jobId = JobId.New();
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);
        var scope = command.Scope;
        var context = command.Context;

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var sqlCommand = new SqlCommand(EnqueueSql, connection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = EvWorkload });
                sqlCommand.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = command.EnvironmentId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = command.SiteName });
                sqlCommand.Parameters.Add(new SqlParameter("@directory", SqlDbType.NVarChar, 260) { Value = command.DirectoryServer });
                sqlCommand.Parameters.Add(new SqlParameter("@requestedBy", SqlDbType.NVarChar, 200) { Value = command.RequestedBy });
                sqlCommand.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.Int) { Value = context.SchemaVersion });
                sqlCommand.Parameters.Add(new SqlParameter("@expProjectCfgVer", SqlDbType.Int) { Value = context.ExpectedProjectConfigurationVersion!.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@expCfgHash", SqlDbType.Char, 64) { Value = context.ExpectedConfigurationHash!.Value.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@policyVersion", SqlDbType.Int) { Value = context.DiscoveryPolicyVersion });
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
    public async Task<ClaimedEvDiscoveryCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var leaseExpires = SqlJobMapping.ToDbUtc(now + leaseDuration);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClaimedEvDiscoveryCommand? result = null;
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

    private static ClaimedEvDiscoveryCommand Read(SqlDataReader reader, TenantScope scope)
    {
        var claimedJob = new ClaimedJob(
            new JobId(reader.GetGuid(0)),
            new LeaseEpoch(reader.GetInt64(1)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(2)),
            reader.GetInt32(3));

        var context = new EvDiscoveryCommandContext(
            reader.GetInt32(8),
            reader.GetInt32(9),
            new Sha256Hash(reader.GetString(10).TrimEnd()),
            reader.GetInt32(11));

        var command = new EvDiscoveryCommand(
            scope,
            new EvEnvironmentId(reader.GetGuid(4)),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            new CorrelationId(reader.GetGuid(12)),
            context);

        return new ClaimedEvDiscoveryCommand(claimedJob, command);
    }
}
