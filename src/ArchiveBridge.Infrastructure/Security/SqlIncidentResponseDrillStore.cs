using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Security;

/// <summary>Persistência do <see cref="IncidentResponseDrillRecord"/> (AB-I7-008). Mesmo padrão de lock/convergência/revalidação de integridade das demais stores deste Passo. RLS por SESSION_CONTEXT — usada pela demonstração de cross-tenant denial.</summary>
public sealed class SqlIncidentResponseDrillStore(TenantConnectionFactory connectionFactory) : IIncidentResponseDrillStore
{
    // Colunas = tenant_id(0), project_id(1), drill_type(2), drill_version(3), outcome(4),
    // started_at_utc(5), completed_at_utc(6), evidence_digest(7), disposition(8), content_fingerprint(9),
    // executed_by(10), executed_by_role(11), correlation_id(12), recorded_at_utc(13), schema_version(14),
    // record_hash(15).
    private const string Columns =
        "tenant_id, project_id, drill_type, drill_version, outcome, started_at_utc, completed_at_utc, " +
        "evidence_digest, disposition, content_fingerprint, executed_by, executed_by_role, correlation_id, " +
        "recorded_at_utc, schema_version, record_hash";

    private const string LockedRecordsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_incident_response_drills WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND drill_type = @drillType
        ORDER BY drill_version DESC;
        """;

    private const string LatestSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_incident_response_drills
        WHERE tenant_id = @tenant AND project_id = @project AND drill_type = @drillType
        ORDER BY drill_version DESC;
        """;

    private const string InsertSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @recordedAt);

        INSERT INTO dbo.security_incident_response_drills ({Columns})
        VALUES
            (@tenant, @project, @drillType, @version, @outcome, @startedAt, @completedAt, @evidenceDigest,
             @disposition, @contentFingerprint, @executedBy, @executedByRole, @correlation, @recordedAt,
             @schemaVersion, @recordHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<IncidentResponseDrillRecord> RecordDrillAsync(
        TenantScope scope,
        IncidentResponseDrillType drillType,
        IncidentResponseDrillOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        Sha256Hash evidenceDigest,
        string disposition,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = IncidentResponseDrillRecord.Record(
            scope.Tenant, scope.Project, drillType, drillVersion: 1, outcome, startedAtUtc, completedAtUtc,
            evidenceDigest, disposition, executedBy, executedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IncidentResponseDrillRecord? current = null;
            await using (var command = new SqlCommand(LockedRecordsSql, connection.Connection, transaction))
            {
                BindScope(command, scope, drillType);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    current = ReadRecord(reader);
                }
            }

            if (current is not null
                && string.Equals(current.ContentFingerprint.Value, candidate.ContentFingerprint.Value, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var nextVersion = (current?.DrillVersion ?? 0) + 1;
            var record = IncidentResponseDrillRecord.Record(
                scope.Tenant, scope.Project, drillType, nextVersion, outcome, startedAtUtc, completedAtUtc,
                evidenceDigest, disposition, executedBy, executedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertSql, connection.Connection, transaction))
            {
                BindRecordParameters(command, scope, drillType, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IncidentResponseDrillRecord?> GetLatestAsync(
        TenantScope scope, IncidentResponseDrillType drillType, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestSql, connection.Connection);
        BindScope(command, scope, drillType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    private static void BindScope(SqlCommand command, TenantScope scope, IncidentResponseDrillType drillType)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@drillType", SqlDbType.TinyInt) { Value = (byte)drillType });
    }

    private static void BindRecordParameters(
        SqlCommand command, TenantScope scope, IncidentResponseDrillType drillType, IncidentResponseDrillRecord record)
    {
        BindScope(command, scope, drillType);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.DrillVersion });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.CompletedAtUtc) });
        command.Parameters.Add(new SqlParameter("@evidenceDigest", SqlDbType.Char, 64) { Value = record.EvidenceDigest.Value });
        command.Parameters.Add(new SqlParameter("@disposition", SqlDbType.NVarChar, 1000) { Value = record.Disposition });
        command.Parameters.Add(new SqlParameter("@contentFingerprint", SqlDbType.Char, 64) { Value = record.ContentFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@executedBy", SqlDbType.NVarChar, 200) { Value = record.ExecutedBy });
        command.Parameters.Add(new SqlParameter("@executedByRole", SqlDbType.NVarChar, 50) { Value = record.ExecutedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.RecordedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = record.RecordHash.Value });
    }

    private static IncidentResponseDrillRecord ReadRecord(SqlDataReader reader)
    {
        var tenant = new TenantId(reader.GetGuid(0));
        var project = new ProjectId(reader.GetGuid(1));
        var drillType = (IncidentResponseDrillType)reader.GetByte(2);
        var drillVersion = reader.GetInt32(3);
        var outcome = (IncidentResponseDrillOutcome)reader.GetByte(4);
        var startedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(5));
        var completedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(6));
        var evidenceDigest = new Sha256Hash(reader.GetString(7).TrimEnd());
        var disposition = reader.GetString(8).TrimEnd();
        var contentFingerprint = new Sha256Hash(reader.GetString(9).TrimEnd());
        var executedBy = reader.GetString(10).TrimEnd();
        var executedByRole = reader.GetString(11).TrimEnd();
        var correlation = new CorrelationId(reader.GetGuid(12));
        var recordedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(13));
        var schemaVersion = reader.GetString(14).TrimEnd();
        var recordHash = new Sha256Hash(reader.GetString(15).TrimEnd());

        return IncidentResponseDrillRecord.Rehydrate(
            tenant, project, drillType, drillVersion, outcome, startedAtUtc, completedAtUtc, evidenceDigest,
            disposition, executedBy, executedByRole, correlation, recordedAtUtc, schemaVersion, contentFingerprint,
            recordHash);
    }
}
