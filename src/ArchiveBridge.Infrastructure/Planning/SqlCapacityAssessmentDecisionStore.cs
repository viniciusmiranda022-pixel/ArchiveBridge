using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Planning;

/// <summary>
/// Registro append-only das decisões de avaliação Microsoft (<c>capacity_assessment_decisions</c>) e
/// da liberação controlada de ondas bloqueadas. Uma aprovação grava, na MESMA transação, a decisão + a
/// transição <c>Blocked → Validating</c> (concorrência otimista por <c>row_version</c> e, se durável,
/// cercamento do Job). Uma rejeição apenas registra a decisão. A role da aplicação só tem SELECT/INSERT
/// nesta tabela (imutável). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlCapacityAssessmentDecisionStore(TenantConnectionFactory connectionFactory, IClock clock)
    : ICapacityAssessmentDecisionStore
{
    private const int ConcurrencyError = 50021;

    private const string InsertDecisionSql =
        """
        INSERT INTO dbo.capacity_assessment_decisions
            (tenant_id, project_id, wave_id, wave_version, target_archive_id, decision,
             evidence_reference, decided_by, decided_at_utc, correlation_id, notes)
        VALUES
            (@dTenant, @dProject, @dWaveId, @dVersion, @dArchive, @dDecision,
             @dEvidence, @dBy, @dAt, @dCorr, @dNotes);
        """;

    private static readonly string UnblockAndInsertSql =
        $"""
        SET NOCOUNT ON;
        {SqlJobFence.GuardSql}
        UPDATE dbo.migration_waves SET status = @status
        WHERE wave_id = @waveId AND project_id = @project AND row_version = @rowVersion;
        IF @@ROWCOUNT = 0 THROW 50021, 'Onda alterada concorrentemente (row_version divergente).', 1;
        {InsertDecisionSql}
        """;

    private static readonly string InsertOnlySql = $"SET NOCOUNT ON;\n{InsertDecisionSql}";

    private const string GetApprovedSql =
        """
        SELECT target_archive_id, decided_by
        FROM dbo.capacity_assessment_decisions
        WHERE wave_id = @waveId AND project_id = @project AND wave_version = @version AND decision = 0;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task RecordAsync(
        MigrationWave wave,
        CapacityAssessmentDecision decision,
        bool unblock,
        JobFence? fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentNullException.ThrowIfNull(decision);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (unblock)
            {
                await using (var command = new SqlCommand(UnblockAndInsertSql, connection.Connection, transaction))
                {
                    command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)wave.Status });
                    command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
                    command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
                    command.Parameters.Add(new SqlParameter("@rowVersion", SqlDbType.Binary, 8) { Value = wave.RowVersion.ToBytes() });
                    SqlJobFence.Bind(command, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                    BindDecision(command, wave, decision);
                    await SqlJobFence.ExecuteGuardedAsync(command, ConcurrencyError, "Onda", cancellationToken).ConfigureAwait(false);
                }

                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await using var command = new SqlCommand(InsertOnlySql, connection.Connection, transaction);
                BindDecision(command, wave, decision);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<TargetArchiveId, string>> GetApprovedAsync(
        TenantScope scope, WaveId waveId, WaveVersion waveVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(GetApprovedSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = waveVersion.Value });

        var approved = new Dictionary<TargetArchiveId, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            approved[new TargetArchiveId(reader.GetString(0))] = reader.GetString(1);
        }

        return approved;
    }

    private static void BindDecision(SqlCommand command, MigrationWave wave, CapacityAssessmentDecision decision)
    {
        command.Parameters.Add(new SqlParameter("@dTenant", SqlDbType.UniqueIdentifier) { Value = wave.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@dProject", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
        command.Parameters.Add(new SqlParameter("@dWaveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
        command.Parameters.Add(new SqlParameter("@dVersion", SqlDbType.Int) { Value = decision.WaveVersion.Value });
        command.Parameters.Add(new SqlParameter("@dArchive", SqlDbType.NVarChar, 320) { Value = decision.Archive.Value });
        command.Parameters.Add(new SqlParameter("@dDecision", SqlDbType.TinyInt) { Value = (byte)decision.Decision });
        command.Parameters.Add(new SqlParameter("@dEvidence", SqlDbType.NVarChar, 200) { Value = decision.EvidenceReference });
        command.Parameters.Add(new SqlParameter("@dBy", SqlDbType.NVarChar, 200) { Value = decision.DecidedBy });
        command.Parameters.Add(new SqlParameter("@dAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(decision.DecidedAtUtc) });
        command.Parameters.Add(new SqlParameter("@dCorr", SqlDbType.UniqueIdentifier) { Value = decision.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@dNotes", SqlDbType.NVarChar, 400) { Value = (object?)decision.Notes ?? DBNull.Value });
    }
}
