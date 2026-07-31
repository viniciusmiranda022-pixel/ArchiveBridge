using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Planning;

/// <summary>
/// Registro append-only das avaliações de capacidade (<c>planning_assessments</c>). Cada linha é
/// evidência imutável (a role da aplicação só tem SELECT/INSERT). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlPlanningStore(TenantConnectionFactory connectionFactory) : IPlanningStore
{
    private const string InsertSql =
        """
        INSERT INTO dbo.planning_assessments
            (tenant_id, project_id, wave_id, wave_version, target_archive_id, total_bytes, rule_code,
             result, reason, correlation_id, assessed_at_utc, released_by)
        VALUES
            (@tenant, @project, @waveId, @version, @archiveId, @totalBytes, @ruleCode,
             @result, @reason, @correlation, @assessedAt, @releasedBy);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task RecordAsync(
        TenantScope scope,
        WaveId waveId,
        WaveVersion waveVersion,
        IReadOnlyList<PlanningAssessment> assessments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assessments);
        if (assessments.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var assessment in assessments)
            {
                await using var command = new SqlCommand(InsertSql, connection.Connection, transaction);
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = waveVersion.Value });
                command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 320) { Value = assessment.Archive.Value });
                command.Parameters.Add(new SqlParameter("@totalBytes", SqlDbType.BigInt) { Value = assessment.TotalBytes });
                command.Parameters.Add(new SqlParameter("@ruleCode", SqlDbType.NVarChar, 64) { Value = assessment.RuleCode });
                command.Parameters.Add(new SqlParameter("@result", SqlDbType.TinyInt) { Value = (byte)assessment.Result });
                command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 400) { Value = assessment.Reason });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = assessment.Correlation.Value });
                command.Parameters.Add(new SqlParameter("@assessedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(assessment.AssessedAtUtc) });
                command.Parameters.Add(new SqlParameter("@releasedBy", SqlDbType.NVarChar, 200)
                { Value = (object?)assessment.ReleasedBy ?? DBNull.Value });
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
}
