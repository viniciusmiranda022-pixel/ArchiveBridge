using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Recovery;

/// <summary>
/// Persistência do <see cref="RecoveryReadinessRecord"/> (AB-I7-005). <see cref="RecordExerciseAsync"/>
/// locka, sob a MESMA transação, os registros já existentes deste escopo (tenant/project/tipo de
/// exercício) e decide sob esse lock se o candidato converge para a versão vigente (mesmo
/// <see cref="RecoveryReadinessRecord.ExerciseFingerprint"/>, replay idempotente) ou se é uma versão
/// realmente nova. Toda leitura revalida <see cref="RecoveryReadinessRecord.RecordHash"/> contra os
/// campos REALMENTE persistidos (fronteira não confiável). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlRecoveryReadinessStore(TenantConnectionFactory connectionFactory) : IRecoveryReadinessStore
{
    // Colunas = tenant_id(0), project_id(1), exercise_type(2), exercise_version(3), status(4),
    // objective(5), objective_threshold_ticks(6), measurement_started_at_utc(7),
    // measurement_completed_at_utc(8), evidence_fingerprint(9), failure_domain(10), notes(11),
    // exercise_fingerprint(12), executed_by(13), executed_by_role(14), correlation_id(15),
    // executed_at_utc(16), schema_version(17), record_hash(18).
    private const string Columns =
        "tenant_id, project_id, exercise_type, exercise_version, status, objective, objective_threshold_ticks, " +
        "measurement_started_at_utc, measurement_completed_at_utc, evidence_fingerprint, failure_domain, notes, " +
        "exercise_fingerprint, executed_by, executed_by_role, correlation_id, executed_at_utc, schema_version, record_hash";

    private const string LockedRecordsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.recovery_readiness_evidence WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND exercise_type = @type
        ORDER BY exercise_version DESC;
        """;

    private const string LatestSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.recovery_readiness_evidence
        WHERE tenant_id = @tenant AND project_id = @project AND exercise_type = @type
        ORDER BY exercise_version DESC;
        """;

    private const string HistorySql =
        $"""
        SELECT {Columns} FROM dbo.recovery_readiness_evidence
        WHERE tenant_id = @tenant AND project_id = @project AND exercise_type = @type
        ORDER BY exercise_version ASC;
        """;

    // FK_rre_project exige que (tenant_id, project_id) já exista em dbo.projects. dbo.projects hoje só é
    // provisionado de forma preguiçosa pela criação de Jobs (SqlJobStore.CreateSql) — um exercício de
    // recovery readiness pode ser o PRIMEIRO evento já registrado para um projeto (ex.: um restore drill
    // ou uma avaliação de HA rodada antes de qualquer Job existir), então a linha de projeto não pode ser
    // presumida. Mesmo padrão IF NOT EXISTS...INSERT já usado por SqlJobStore.CreateSql,
    // SqlPlanningCommandInbox, SqlEvDiscoveryCommandInbox e SqlPortalOperationalAudit; @tenant/@project
    // aqui vêm sempre do TenantScope do chamador, nunca de um recurso não verificado.
    private const string InsertSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @executedAt);

        INSERT INTO dbo.recovery_readiness_evidence ({Columns})
        VALUES
            (@tenant, @project, @type, @version, @status, @objective, @thresholdTicks, @measurementStart,
             @measurementEnd, @evidenceFingerprint, @failureDomain, @notes, @exerciseFingerprint, @executedBy,
             @executedByRole, @correlation, @executedAt, @schemaVersion, @recordHash);
        """;

    private const string InsertAuditEventSql =
        """
        INSERT INTO dbo.recovery_readiness_audit_events
            (tenant_id, project_id, exercise_type, exercise_version, event_type, actor_id, actor_role, reason,
             correlation_id, occurred_at_utc)
        VALUES
            (@tenant, @project, @type, @version, @eventType, @actorId, @actorRole, @reason, @correlation, @occurredAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<RecoveryReadinessRecord> RecordExerciseAsync(
        TenantScope scope,
        RecoveryExerciseType exerciseType,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Validação/normalização fail-fast ANTES de abrir a transação — a versão 1 aqui é só um
        // placeholder para computar o fingerprint/normalizar os campos; a versão REAL é alocada sob
        // lock abaixo (mesma técnica de SqlReconciliationCertificateStore).
        var candidate = BuildRecord(
            scope, exerciseType, exerciseVersion: 1, status, objective, objectiveThreshold, measurement,
            evidenceFingerprint, failureDomain, notes, executedBy, executedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecoveryReadinessRecord? current = null;
            await using (var command = new SqlCommand(LockedRecordsSql, connection.Connection, transaction))
            {
                BindScope(command, scope, exerciseType);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    current = ReadRecord(reader);
                }
            }

            if (current is not null
                && string.Equals(current.ExerciseFingerprint.Value, candidate.ExerciseFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico: converge sem inserir uma nova versão, mesmo sob concorrência —
                // execuções concorrentes idênticas do MESMO exercício convergem todas para a MESMA
                // versão vigente.
                await InsertAuditEventAsync(
                    connection.Connection, transaction, scope, exerciseType, current.ExerciseVersion,
                    RecoveryReadinessAuditEventType.Converged, executedBy, executedByRole,
                    "Exercício convergiu idempotentemente para a versão vigente (replay idêntico).", correlation, now,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var nextVersion = (current?.ExerciseVersion ?? 0) + 1;
            var record = BuildRecord(
                scope, exerciseType, nextVersion, status, objective, objectiveThreshold, measurement,
                evidenceFingerprint, failureDomain, notes, executedBy, executedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertSql, connection.Connection, transaction))
            {
                BindRecordParameters(command, scope, exerciseType, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertAuditEventAsync(
                connection.Connection, transaction, scope, exerciseType, record.ExerciseVersion,
                RecoveryReadinessAuditEventType.Issued, executedBy, executedByRole,
                "Novo registro de recovery readiness emitido.", correlation, now, cancellationToken).ConfigureAwait(false);

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
    public async Task<RecoveryReadinessRecord?> GetLatestAsync(
        TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestSql, connection.Connection);
        BindScope(command, scope, exerciseType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecoveryReadinessRecord>> GetHistoryAsync(
        TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var history = new List<RecoveryReadinessRecord>();
        await using var command = new SqlCommand(HistorySql, connection.Connection);
        BindScope(command, scope, exerciseType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(ReadRecord(reader));
        }

        return history;
    }

    private static RecoveryReadinessRecord BuildRecord(
        TenantScope scope,
        RecoveryExerciseType exerciseType,
        int exerciseVersion,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now) =>
        status switch
        {
            RecoveryReadinessStatus.Pass => RecoveryReadinessRecord.Pass(
                scope.Tenant, scope.Project, exerciseType, exerciseVersion, objective, objectiveThreshold,
                measurement ?? throw new RecoveryReadinessObjectiveNotMetException("Pass exige uma medição real do exercício."),
                evidenceFingerprint, notes, executedBy, executedByRole, correlation, now),
            RecoveryReadinessStatus.Blocked => RecoveryReadinessRecord.Blocked(
                scope.Tenant, scope.Project, exerciseType, exerciseVersion, objective, objectiveThreshold, measurement,
                evidenceFingerprint, failureDomain, notes, executedBy, executedByRole, correlation, now),
            RecoveryReadinessStatus.NotMeasured => RecoveryReadinessRecord.NotMeasured(
                scope.Tenant, scope.Project, exerciseType, exerciseVersion, objective, objectiveThreshold, notes,
                executedBy, executedByRole, correlation, now),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Status de recovery readiness desconhecido."),
        };

    private static async Task InsertAuditEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TenantScope scope,
        RecoveryExerciseType exerciseType,
        int? exerciseVersion,
        RecoveryReadinessAuditEventType eventType,
        string actorId,
        string actorRole,
        string reason,
        CorrelationId correlation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(InsertAuditEventSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.TinyInt) { Value = (byte)exerciseType });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = (object?)exerciseVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@eventType", SqlDbType.TinyInt) { Value = (byte)eventType });
        command.Parameters.Add(new SqlParameter("@actorId", SqlDbType.NVarChar, 200) { Value = actorId });
        command.Parameters.Add(new SqlParameter("@actorRole", SqlDbType.NVarChar, 50) { Value = actorRole });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 500) { Value = reason });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
        command.Parameters.Add(new SqlParameter("@occurredAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(occurredAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindScope(SqlCommand command, TenantScope scope, RecoveryExerciseType exerciseType)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.TinyInt) { Value = (byte)exerciseType });
    }

    private static void BindRecordParameters(
        SqlCommand command, TenantScope scope, RecoveryExerciseType exerciseType, RecoveryReadinessRecord record)
    {
        BindScope(command, scope, exerciseType);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.ExerciseVersion });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)record.Status });
        command.Parameters.Add(new SqlParameter("@objective", SqlDbType.TinyInt) { Value = (byte)record.Objective });
        command.Parameters.Add(new SqlParameter("@thresholdTicks", SqlDbType.BigInt)
        {
            Value = record.ObjectiveThreshold is { } threshold ? threshold.Ticks : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@measurementStart", SqlDbType.DateTime2)
        {
            Value = record.Measurement is { } started ? SqlJobMapping.ToDbUtc(started.StartedAtUtc) : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@measurementEnd", SqlDbType.DateTime2)
        {
            Value = record.Measurement is { } completed ? SqlJobMapping.ToDbUtc(completed.CompletedAtUtc) : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@evidenceFingerprint", SqlDbType.Char, 64) { Value = record.EvidenceFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@failureDomain", SqlDbType.NVarChar, 1000) { Value = record.FailureDomain });
        command.Parameters.Add(new SqlParameter("@notes", SqlDbType.NVarChar, 1000) { Value = record.Notes });
        command.Parameters.Add(new SqlParameter("@exerciseFingerprint", SqlDbType.Char, 64) { Value = record.ExerciseFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@executedBy", SqlDbType.NVarChar, 200) { Value = record.ExecutedBy });
        command.Parameters.Add(new SqlParameter("@executedByRole", SqlDbType.NVarChar, 50) { Value = record.ExecutedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@executedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.ExecutedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = record.RecordHash.Value });
    }

    private static RecoveryReadinessRecord ReadRecord(SqlDataReader reader)
    {
        var tenant = new TenantId(reader.GetGuid(0));
        var project = new ProjectId(reader.GetGuid(1));
        var exerciseType = (RecoveryExerciseType)reader.GetByte(2);
        var exerciseVersion = reader.GetInt32(3);
        var status = (RecoveryReadinessStatus)reader.GetByte(4);
        var objective = (RecoveryObjective)reader.GetByte(5);
        var objectiveThreshold = reader.IsDBNull(6) ? (TimeSpan?)null : TimeSpan.FromTicks(reader.GetInt64(6));
        RecoveryObjectiveMeasurement? measurement = reader.IsDBNull(7) || reader.IsDBNull(8)
            ? null
            : new RecoveryObjectiveMeasurement(SqlJobMapping.ReadUtc(reader.GetDateTime(7)), SqlJobMapping.ReadUtc(reader.GetDateTime(8)));
        var evidenceFingerprint = new Sha256Hash(reader.GetString(9).TrimEnd());
        var failureDomain = reader.GetString(10).TrimEnd();
        var notes = reader.GetString(11).TrimEnd();
        var exerciseFingerprint = new Sha256Hash(reader.GetString(12).TrimEnd());
        var executedBy = reader.GetString(13).TrimEnd();
        var executedByRole = reader.GetString(14).TrimEnd();
        var correlation = new CorrelationId(reader.GetGuid(15));
        var executedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(16));
        var schemaVersion = reader.GetString(17).TrimEnd();
        var recordHash = new Sha256Hash(reader.GetString(18).TrimEnd());

        return RecoveryReadinessRecord.Rehydrate(
            tenant, project, exerciseType, exerciseVersion, status, objective, objectiveThreshold, measurement,
            evidenceFingerprint, failureDomain, notes, exerciseFingerprint, executedBy, executedByRole, correlation,
            executedAtUtc, schemaVersion, recordHash);
    }
}
