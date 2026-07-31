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
/// comando (<c>planning_commands</c>) — nunca há Job sem operação nem operação sem Job. A
/// reivindicação reutiliza o claim atômico com fencing por época do <see cref="IJobStore"/>.
/// </summary>
public sealed class SqlPlanningCommandInbox(TenantConnectionFactory connectionFactory, IJobStore jobStore, IClock clock)
    : IPlanningCommandInbox
{
    private const byte ControlWorkload = (byte)Workload.Control;

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
            (job_id, tenant_id, project_id, command_type, wave_id, content_code_page, generated_by, correlation_id, created_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, @commandType, @waveId, @codePage, @generatedBy, @correlation, @now);
        """;

    private const string LoadContextSql =
        """
        SELECT command_type, wave_id, content_code_page, generated_by, correlation_id
        FROM dbo.planning_commands
        WHERE job_id = @jobId;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IJobStore _jobStore = jobStore;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<JobId> EnqueueAsync(PlanningCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var jobId = JobId.New();
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);
        var scope = command.Scope;

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
        var claimed = await _jobStore.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Control, worker, leaseDuration, correlation), cancellationToken)
            .ConfigureAwait(false);
        if (claimed is null)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(LoadContextSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = claimed.JobId.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Job de controle reivindicado sem contexto de comando de planejamento associado.");
        }

        var planningCommand = new PlanningCommand(
            (PlanningCommandKind)reader.GetByte(0),
            scope,
            reader.IsDBNull(1) ? null : new WaveId(reader.GetGuid(1)),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            new CorrelationId(reader.GetGuid(4)));

        return new ClaimedPlanningCommand(claimed, planningCommand);
    }
}
