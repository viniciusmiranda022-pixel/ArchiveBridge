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

/// <summary>
/// Persistência do <see cref="WorkerHardeningControlRecord"/> (AB-I7-008). Mesmo padrão de
/// <see cref="ArchiveBridge.Infrastructure.Recovery.SqlRecoveryReadinessStore"/>: lock sob a mesma
/// transação, convergência idempotente por <see cref="WorkerHardeningControlRecord.ContentFingerprint"/>,
/// revalidação de integridade em toda leitura. RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlWorkerHardeningBaselineStore(TenantConnectionFactory connectionFactory) : IWorkerHardeningBaselineStore
{
    // Colunas = tenant_id(0), project_id(1), control(2), control_version(3), status(4),
    // measurement_measured_at_utc(5), measurement_method(6), evidence_fingerprint(7), blocked_reason(8),
    // notes(9), content_fingerprint(10), executed_by(11), executed_by_role(12), correlation_id(13),
    // executed_at_utc(14), schema_version(15), record_hash(16).
    private const string Columns =
        "tenant_id, project_id, control, control_version, status, measurement_measured_at_utc, measurement_method, " +
        "evidence_fingerprint, blocked_reason, notes, content_fingerprint, executed_by, executed_by_role, " +
        "correlation_id, executed_at_utc, schema_version, record_hash";

    private const string LockedRecordsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_worker_hardening_evidence WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND control = @control
        ORDER BY control_version DESC;
        """;

    private const string LatestSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_worker_hardening_evidence
        WHERE tenant_id = @tenant AND project_id = @project AND control = @control
        ORDER BY control_version DESC;
        """;

    private const string LatestForAllControlsSql =
        $"""
        SELECT {Columns} FROM dbo.security_worker_hardening_evidence AS whe
        WHERE tenant_id = @tenant AND project_id = @project
          AND control_version = (
              SELECT MAX(inner_whe.control_version) FROM dbo.security_worker_hardening_evidence AS inner_whe
              WHERE inner_whe.tenant_id = whe.tenant_id AND inner_whe.project_id = whe.project_id
                AND inner_whe.control = whe.control)
        ORDER BY control;
        """;

    private const string InsertSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @executedAt);

        INSERT INTO dbo.security_worker_hardening_evidence ({Columns})
        VALUES
            (@tenant, @project, @control, @version, @status, @measuredAt, @measurementMethod, @evidenceFingerprint,
             @blockedReason, @notes, @contentFingerprint, @executedBy, @executedByRole, @correlation, @executedAt,
             @schemaVersion, @recordHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<WorkerHardeningControlRecord> RecordControlAsync(
        TenantScope scope,
        WorkerHardeningControl control,
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = BuildRecord(
            scope, control, controlVersion: 1, status, measurement, evidenceFingerprint, blockedReason, notes,
            executedBy, executedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerHardeningControlRecord? current = null;
            await using (var command = new SqlCommand(LockedRecordsSql, connection.Connection, transaction))
            {
                BindScope(command, scope, control);
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

            var nextVersion = (current?.ControlVersion ?? 0) + 1;
            var record = BuildRecord(
                scope, control, nextVersion, status, measurement, evidenceFingerprint, blockedReason, notes,
                executedBy, executedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertSql, connection.Connection, transaction))
            {
                BindRecordParameters(command, scope, control, record);
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
    public async Task<WorkerHardeningControlRecord?> GetLatestAsync(
        TenantScope scope, WorkerHardeningControl control, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestSql, connection.Connection);
        BindScope(command, scope, control);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkerHardeningControlRecord>> GetLatestForAllControlsAsync(
        TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var results = new List<WorkerHardeningControlRecord>();
        await using var command = new SqlCommand(LatestForAllControlsSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    private static WorkerHardeningControlRecord BuildRecord(
        TenantScope scope,
        WorkerHardeningControl control,
        int controlVersion,
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now) =>
        status switch
        {
            WorkerHardeningStatus.Pass => WorkerHardeningControlRecord.Pass(
                scope.Tenant, scope.Project, control, controlVersion,
                measurement ?? throw new WorkerHardeningInvariantViolationException("Pass exige uma medição real do controle."),
                evidenceFingerprint, notes, executedBy, executedByRole, correlation, now),
            WorkerHardeningStatus.Blocked => WorkerHardeningControlRecord.Blocked(
                scope.Tenant, scope.Project, control, controlVersion, measurement, evidenceFingerprint, blockedReason,
                notes, executedBy, executedByRole, correlation, now),
            WorkerHardeningStatus.NotMeasured => WorkerHardeningControlRecord.NotMeasured(
                scope.Tenant, scope.Project, control, controlVersion, notes, executedBy, executedByRole, correlation, now),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Status de worker hardening desconhecido."),
        };

    private static void BindScope(SqlCommand command, TenantScope scope, WorkerHardeningControl control)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@control", SqlDbType.TinyInt) { Value = (byte)control });
    }

    private static void BindRecordParameters(
        SqlCommand command, TenantScope scope, WorkerHardeningControl control, WorkerHardeningControlRecord record)
    {
        BindScope(command, scope, control);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.ControlVersion });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)record.Status });
        command.Parameters.Add(new SqlParameter("@measuredAt", SqlDbType.DateTime2)
        {
            Value = record.Measurement is { } measurement ? SqlJobMapping.ToDbUtc(measurement.MeasuredAtUtc) : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@measurementMethod", SqlDbType.NVarChar, 200)
        {
            Value = record.Measurement is { } method ? method.MeasurementMethod : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@evidenceFingerprint", SqlDbType.Char, 64) { Value = record.EvidenceFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@blockedReason", SqlDbType.NVarChar, 1000) { Value = record.BlockedReason });
        command.Parameters.Add(new SqlParameter("@notes", SqlDbType.NVarChar, 1000) { Value = record.Notes });
        command.Parameters.Add(new SqlParameter("@contentFingerprint", SqlDbType.Char, 64) { Value = record.ContentFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@executedBy", SqlDbType.NVarChar, 200) { Value = record.ExecutedBy });
        command.Parameters.Add(new SqlParameter("@executedByRole", SqlDbType.NVarChar, 50) { Value = record.ExecutedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@executedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.ExecutedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = record.RecordHash.Value });
    }

    private static WorkerHardeningControlRecord ReadRecord(SqlDataReader reader)
    {
        var tenant = new TenantId(reader.GetGuid(0));
        var project = new ProjectId(reader.GetGuid(1));
        var control = (WorkerHardeningControl)reader.GetByte(2);
        var controlVersion = reader.GetInt32(3);
        var status = (WorkerHardeningStatus)reader.GetByte(4);
        WorkerHardeningMeasurement? measurement = reader.IsDBNull(5) || reader.IsDBNull(6)
            ? null
            : new WorkerHardeningMeasurement(SqlJobMapping.ReadUtc(reader.GetDateTime(5)), reader.GetString(6).TrimEnd());
        var evidenceFingerprint = new Sha256Hash(reader.GetString(7).TrimEnd());
        var blockedReason = reader.GetString(8).TrimEnd();
        var notes = reader.GetString(9).TrimEnd();
        var contentFingerprint = new Sha256Hash(reader.GetString(10).TrimEnd());
        var executedBy = reader.GetString(11).TrimEnd();
        var executedByRole = reader.GetString(12).TrimEnd();
        var correlation = new CorrelationId(reader.GetGuid(13));
        var executedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(14));
        var schemaVersion = reader.GetString(15).TrimEnd();
        var recordHash = new Sha256Hash(reader.GetString(16).TrimEnd());

        return WorkerHardeningControlRecord.Rehydrate(
            tenant, project, control, controlVersion, status, measurement, evidenceFingerprint, blockedReason, notes,
            executedBy, executedByRole, correlation, executedAtUtc, schemaVersion, contentFingerprint, recordHash);
    }
}
