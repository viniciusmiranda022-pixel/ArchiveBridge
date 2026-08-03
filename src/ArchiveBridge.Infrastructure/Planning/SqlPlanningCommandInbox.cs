using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Planning;

/// <summary>
/// Fila durável de comandos de planejamento sobre os Jobs do Slice 1. O enfileiramento é ATÔMICO:
/// numa única transação cria o Job de controle (Pending) + a transição inicial + o contexto do
/// comando VINCULADO À VERSÃO (<c>planning_commands</c>) — nunca há Job sem operação nem operação sem
/// Job. A reivindicação é EXCLUSIVA de comandos de planejamento: o claim atômico (READPAST/UPDLOCK,
/// fencing por época) só considera Jobs de controle que POSSUAM contexto em <c>planning_commands</c>
/// (via <c>EXISTS</c>) e carrega esse contexto na MESMA operação — um Job de controle sem contexto não
/// é reivindicado (não fica preso em Processing) e nenhum outro tipo de Job é capturado.
/// </summary>
public sealed class SqlPlanningCommandInbox(TenantConnectionFactory connectionFactory, IClock clock)
    : IPlanningCommandInbox
{
    private const byte ControlWorkload = (byte)Workload.Control;

    // Comandos de controle são de baixo volume e prioridade 0: ordenação FIFO por criação (aging
    // desprezível, janela ampla) — consistente com a fila durável, sem starvation prático.
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

        INSERT INTO dbo.planning_commands
            (job_id, tenant_id, project_id, command_type, wave_id, content_code_page, generated_by, correlation_id, created_at_utc,
             command_schema_version, expected_project_configuration_version, expected_configuration_hash,
             expected_wave_version, expected_selection_hash, expected_target_root_folder,
             mapping_schema_version, mapping_generator_version, mapping_policy_version)
        VALUES
            (@jobId, @tenant, @projectId, @commandType, @waveId, @codePage, @generatedBy, @correlation, @now,
             @schemaVersion, @expProjectCfgVer, @expCfgHash,
             @expWaveVer, @expSelHash, @expTarget,
             @mapSchemaVer, @mapGenVer, @mapPolicyVer);
        """;

    // Claim EXCLUSIVO: só Jobs de controle COM contexto de planejamento (EXISTS). Reivindica sob
    // fencing (época+1) e carrega o contexto na mesma unidade. Sem contexto ⇒ não reivindicado.
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
              AND EXISTS (SELECT 1 FROM dbo.planning_commands pc
                          WHERE pc.job_id = j.job_id AND pc.tenant_id = @tenant AND pc.project_id = j.project_id)
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
               pc.command_type, pc.wave_id, pc.content_code_page, pc.generated_by, pc.correlation_id,
               pc.command_schema_version, pc.expected_project_configuration_version, pc.expected_configuration_hash,
               pc.expected_wave_version, pc.expected_selection_hash, pc.expected_target_root_folder,
               pc.mapping_schema_version, pc.mapping_generator_version, pc.mapping_policy_version
        FROM @claimed c INNER JOIN dbo.planning_commands pc ON pc.job_id = c.job_id;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<JobId> EnqueueAsync(PlanningCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var jobId = JobId.New();
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);
        var scope = command.Scope;
        var context = command.Context;

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var sqlCommand = new SqlCommand(EnqueueSql, connection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = ControlWorkload });
                sqlCommand.Parameters.Add(new SqlParameter("@commandType", SqlDbType.TinyInt) { Value = (byte)command.Kind });
                sqlCommand.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier)
                { Value = command.Wave is { } wave ? wave.Value : DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@codePage", SqlDbType.Int)
                { Value = (object?)command.ContentCodePage ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@generatedBy", SqlDbType.NVarChar, 200)
                { Value = (object?)command.GeneratedBy ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = command.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
                sqlCommand.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.Int) { Value = context.SchemaVersion });
                sqlCommand.Parameters.Add(new SqlParameter("@expProjectCfgVer", SqlDbType.Int)
                { Value = (object?)context.ExpectedProjectConfigurationVersion ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@expCfgHash", SqlDbType.Char, 64)
                { Value = context.ExpectedConfigurationHash is { } cfg ? cfg.Value : DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@expWaveVer", SqlDbType.Int)
                { Value = (object?)context.ExpectedWaveVersion ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@expSelHash", SqlDbType.Char, 64)
                { Value = context.ExpectedSelectionHash is { } sel ? sel.Value : DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@expTarget", SqlDbType.NVarChar, 400)
                { Value = (object?)context.ExpectedTargetRootFolder ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@mapSchemaVer", SqlDbType.Int)
                { Value = (object?)context.MappingSchemaVersion ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@mapGenVer", SqlDbType.Int)
                { Value = (object?)context.MappingGeneratorVersion ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@mapPolicyVer", SqlDbType.Int)
                { Value = (object?)context.MappingPolicyVersion ?? DBNull.Value });
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
    public async Task<ClaimedPlanningCommand?> TryClaimNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var leaseExpires = SqlJobMapping.ToDbUtc(now + leaseDuration);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClaimedPlanningCommand? result = null;
            await using (var command = new SqlCommand(ClaimSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = ControlWorkload });
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

    private static ClaimedPlanningCommand Read(SqlDataReader reader, TenantScope scope)
    {
        var claimedJob = new ClaimedJob(
            new JobId(reader.GetGuid(0)),
            new LeaseEpoch(reader.GetInt64(1)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(2)),
            reader.GetInt32(3));

        var context = new PlanningCommandContext(
            reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : new Sha256Hash(reader.GetString(11).TrimEnd()),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.IsDBNull(13) ? null : new Sha256Hash(reader.GetString(13).TrimEnd()),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15),
            reader.IsDBNull(16) ? null : reader.GetInt32(16),
            reader.IsDBNull(17) ? null : reader.GetInt32(17));

        var command = new PlanningCommand(
            (PlanningCommandKind)reader.GetByte(4),
            scope,
            reader.IsDBNull(5) ? null : new WaveId(reader.GetGuid(5)),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            new CorrelationId(reader.GetGuid(8)),
            context);

        return new ClaimedPlanningCommand(claimedJob, command);
    }
}
