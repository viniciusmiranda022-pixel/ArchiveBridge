using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Persistência do workflow de disposition humano/auditável sobre exceções técnicas de reconciliação
/// (AB-I6-010). <see cref="SaveDecisionAsync"/> locka, sob a MESMA transação: (1) a linha da avaliação de
/// reconciliação vigente do escopo (onda/plano) — detectando staleness (item 8) mesmo sob concorrência com
/// <see cref="ServiceResult.SqlReconciliationAssessmentStore.PersistAsync"/> (ambos usam
/// <c>WITH (UPDLOCK, HOLDLOCK)</c> sobre a mesma faixa de linhas, serializando as duas operações); e (2)
/// TODAS as decisões já existentes desta exceção específica, decidindo sob esse lock se o candidato converge
/// para a decisão vigente (mesmo <see cref="ReconciliationExceptionDecision.DecisionFingerprint"/>, item 9 —
/// replay idempotente) ou se é uma decisão realmente nova — caso em que a versão esperada pelo chamador
/// precisa corresponder exatamente à vigente sob o lock, ou a chamada é recusada com
/// <see cref="ConcurrencyException"/> (item 10 — decisões conflitantes concorrentes nunca resolvidas por
/// last-write-wins silencioso). Toda leitura revalida <see cref="ReconciliationExceptionDecision.DecisionFingerprint"/>/
/// <see cref="ReconciliationExceptionDecision.DecisionHash"/> contra os campos REALMENTE persistidos
/// (fronteira não confiável, mesmo princípio de <see cref="Reconciliation.SqlReconciliationAssessmentStore"/>).
/// RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlReconciliationExceptionDispositionStore(TenantConnectionFactory connectionFactory) : IReconciliationExceptionDispositionStore
{
    private const string ResolveAttemptSequenceSql =
        "SELECT attempt_sequence FROM dbo.purview_import_job_plans WHERE wave_id = @wave AND project_id = @project AND planned_job_name = @name;";

    private const string LatestAssessmentVersionForUpdateSql =
        """
        SELECT MAX(assessment_version) FROM dbo.purview_reconciliation_assessments WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project;
        """;

    // Columns = wave_id(0), attempt_sequence(1), assessment_version(2), item_kind(3), item_key(4),
    // decision_version(5), tenant_id(6), project_id(7), assessment_source_fingerprint(8),
    // technical_disposition(9), status(10), reason_code(11), reason_code_catalog_version(12), comment(13),
    // decided_by(14), decided_by_role(15), correlation_id(16), decided_at_utc(17), decision_fingerprint(18),
    // decision_hash(19).
    private const string Columns =
        "wave_id, attempt_sequence, assessment_version, item_kind, item_key, decision_version, tenant_id, project_id, " +
        "assessment_source_fingerprint, technical_disposition, status, reason_code, reason_code_catalog_version, comment, " +
        "decided_by, decided_by_role, correlation_id, decided_at_utc, decision_fingerprint, decision_hash";

    private const string LockedDecisionsSql =
        $"""
        SELECT {Columns} FROM dbo.purview_reconciliation_exception_dispositions WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version
          AND item_kind = @kind AND item_key = @key AND project_id = @project
        ORDER BY decision_version DESC;
        """;

    private const string CurrentDecisionSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.purview_reconciliation_exception_dispositions
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version
          AND item_kind = @kind AND item_key = @key AND project_id = @project
        ORDER BY decision_version DESC;
        """;

    private const string HistorySql =
        $"""
        SELECT {Columns} FROM dbo.purview_reconciliation_exception_dispositions
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version
          AND item_kind = @kind AND item_key = @key AND project_id = @project
        ORDER BY decision_version ASC;
        """;

    private const string CurrentDecisionsForAssessmentSql =
        $"""
        WITH ranked AS
        (
            SELECT {Columns}, ROW_NUMBER() OVER (PARTITION BY item_kind, item_key ORDER BY decision_version DESC) AS rn
            FROM dbo.purview_reconciliation_exception_dispositions
            WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version AND project_id = @project
        )
        SELECT {Columns} FROM ranked WHERE rn = 1;
        """;

    private const string InsertDecisionSql =
        $"""
        INSERT INTO dbo.purview_reconciliation_exception_dispositions ({Columns})
        VALUES
            (@wave, @attempt, @version, @kind, @key, @decisionVersion, @tenant, @project,
             @assessmentFingerprint, @technicalDisposition, @status, @reasonCode, @catalogVersion, @comment,
             @decidedBy, @decidedByRole, @correlation, @decidedAt, @fingerprint, @hash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<ReconciliationExceptionDecision> SaveDecisionAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        int expectedCurrentDecisionVersion,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy,
        string decidedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidateFingerprint = ReconciliationExceptionDecision.ComputeDecisionFingerprint(
            scope.Tenant, scope.Project, wave, plannedJobName, assessmentVersion, itemKind, itemKey, technicalDisposition,
            status, reasonCode, reasonCodeCatalogVersion, comment, decidedBy);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempt = await ResolveAttemptSequenceAsync(connection.Connection, transaction, scope, wave, plannedJobName, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new PurviewImportJobSourceNotFoundException("Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

            // Item 8: revalida, SOB LOCK e na MESMA transação, que a versão de avaliação referenciada
            // continua sendo a vigente — serializa com SqlReconciliationAssessmentStore.PersistAsync via a
            // MESMA técnica de UPDLOCK/HOLDLOCK sobre a faixa de linhas do escopo.
            int? latestAssessmentVersion;
            await using (var command = new SqlCommand(LatestAssessmentVersionForUpdateSql, connection.Connection, transaction))
            {
                BindAssessmentScope(command, wave, attempt, scope.Project);
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                latestAssessmentVersion = scalar is int value ? value : null;
            }

            if (latestAssessmentVersion != assessmentVersion)
            {
                throw new ReconciliationExceptionStaleAssessmentException(
                    "A avaliação de reconciliação referenciada não é mais a vigente (foi superseded) — a disposition " +
                    "sobre a avaliação antiga é recusada (fail-closed).");
            }

            int currentVersion = 0;
            ReconciliationExceptionDecision? current = null;
            await using (var command = new SqlCommand(LockedDecisionsSql, connection.Connection, transaction))
            {
                BindExceptionScope(command, wave, attempt, assessmentVersion, itemKind, itemKey, scope.Project);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    current = ReadDecision(reader, plannedJobName);
                    currentVersion = current.DecisionVersion;
                }
            }

            if (current is not null && string.Equals(current.DecisionFingerprint.Value, candidateFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico da decisão vigente (item 9): converge sem inserir uma nova versão,
                // independentemente da versão esperada pelo chamador — mesmo sob concorrência (item 11: N
                // chamadas concorrentes idênticas convergem todas para a MESMA versão vigente).
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            if (expectedCurrentDecisionVersion != currentVersion)
            {
                // Item 10: uma decisão conflitante (diferente da esperada pelo chamador) já é a vigente sob
                // o lock — recusada fail-closed em vez de sobrescrever silenciosamente (last-write-wins).
                throw new ConcurrencyException(
                    "A decisão vigente sobre esta exceção de reconciliação mudou desde a última leitura; releia o " +
                    "estado atual antes de decidir novamente.");
            }

            var nextVersion = currentVersion + 1;
            var decision = ReconciliationExceptionDecision.Create(
                scope.Tenant, scope.Project, wave, plannedJobName, assessmentVersion, assessmentSourceFingerprint,
                itemKind, itemKey, technicalDisposition, nextVersion, status, reasonCode, reasonCodeCatalogVersion,
                comment, decidedBy, decidedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertDecisionSql, connection.Connection, transaction))
            {
                BindExceptionScope(command, wave, attempt, assessmentVersion, itemKind, itemKey, scope.Project);
                command.Parameters.Add(new SqlParameter("@decisionVersion", SqlDbType.Int) { Value = decision.DecisionVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@assessmentFingerprint", SqlDbType.Char, 64) { Value = decision.AssessmentSourceFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@technicalDisposition", SqlDbType.TinyInt) { Value = (byte)decision.TechnicalDisposition });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)decision.Status });
                command.Parameters.Add(new SqlParameter("@reasonCode", SqlDbType.TinyInt) { Value = (byte)decision.ReasonCode });
                command.Parameters.Add(new SqlParameter("@catalogVersion", SqlDbType.TinyInt) { Value = decision.ReasonCodeCatalogVersion });
                command.Parameters.Add(new SqlParameter("@comment", SqlDbType.NVarChar, 500) { Value = (object?)decision.Comment ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@decidedBy", SqlDbType.NVarChar, 200) { Value = decision.DecidedBy });
                command.Parameters.Add(new SqlParameter("@decidedByRole", SqlDbType.NVarChar, 50) { Value = decision.DecidedByRole });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = decision.Correlation.Value });
                command.Parameters.Add(new SqlParameter("@decidedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(decision.DecidedAtUtc) });
                command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = decision.DecisionFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = decision.DecisionHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return decision;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ReconciliationExceptionDecision?> GetCurrentAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion,
        ReconciliationExceptionItemKind itemKind, string itemKey, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return null;
        }

        await using var command = new SqlCommand(CurrentDecisionSql, connection.Connection);
        BindExceptionScope(command, wave, attempt.Value, assessmentVersion, itemKind, itemKey, scope.Project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDecision(reader, plannedJobName) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReconciliationExceptionDecision>> GetHistoryAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion,
        ReconciliationExceptionItemKind itemKind, string itemKey, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return [];
        }

        var history = new List<ReconciliationExceptionDecision>();
        await using var command = new SqlCommand(HistorySql, connection.Connection);
        BindExceptionScope(command, wave, attempt.Value, assessmentVersion, itemKind, itemKey, scope.Project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(ReadDecision(reader, plannedJobName));
        }

        return history;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReconciliationExceptionDecision>> GetCurrentDecisionsForAssessmentAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return [];
        }

        var decisions = new List<ReconciliationExceptionDecision>();
        await using var command = new SqlCommand(CurrentDecisionsForAssessmentSql, connection.Connection);
        BindAssessmentVersionScope(command, wave, attempt.Value, assessmentVersion, scope.Project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            decisions.Add(ReadDecision(reader, plannedJobName));
        }

        return decisions;
    }

    private static async Task<int?> ResolveAttemptSequenceAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName,
        CancellationToken cancellationToken)
    {
        await using var command = transaction is null
            ? new SqlCommand(ResolveAttemptSequenceSql, connection)
            : new SqlCommand(ResolveAttemptSequenceSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, 100) { Value = plannedJobName.Value });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int value ? value : null;
    }

    private static void BindAssessmentScope(SqlCommand command, WaveId wave, int attempt, ProjectId project)
    {
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
    }

    private static void BindAssessmentVersionScope(SqlCommand command, WaveId wave, int attempt, int assessmentVersion, ProjectId project)
    {
        BindAssessmentScope(command, wave, attempt, project);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessmentVersion });
    }

    private static void BindExceptionScope(
        SqlCommand command, WaveId wave, int attempt, int assessmentVersion, ReconciliationExceptionItemKind itemKind, string itemKey, ProjectId project)
    {
        BindAssessmentVersionScope(command, wave, attempt, assessmentVersion, project);
        command.Parameters.Add(new SqlParameter("@kind", SqlDbType.TinyInt) { Value = (byte)itemKind });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 320) { Value = itemKey });
    }

    private static ReconciliationExceptionDecision ReadDecision(SqlDataReader reader, PurviewImportJobName plannedJobName) =>
        ReconciliationExceptionDecision.Rehydrate(
            new TenantId(reader.GetGuid(6)),
            new ProjectId(reader.GetGuid(7)),
            new WaveId(reader.GetGuid(0)),
            plannedJobName,
            reader.GetInt32(2),
            new Sha256Hash(reader.GetString(8).TrimEnd()),
            (ReconciliationExceptionItemKind)reader.GetByte(3),
            reader.GetString(4).TrimEnd(),
            (ReconciliationDisposition)reader.GetByte(9),
            reader.GetInt32(5),
            (ReconciliationExceptionDecisionStatus)reader.GetByte(10),
            (ReconciliationExceptionReasonCode)reader.GetByte(11),
            reader.GetByte(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetString(14).TrimEnd(),
            reader.GetString(15).TrimEnd(),
            new CorrelationId(reader.GetGuid(16)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(17)),
            new Sha256Hash(reader.GetString(18).TrimEnd()),
            new Sha256Hash(reader.GetString(19).TrimEnd()));
}
