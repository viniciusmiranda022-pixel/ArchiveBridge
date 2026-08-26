using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Persistência do reconciliation certificate (AB-I6-013). <see cref="IssueOrConvergeAsync"/> locka, sob a
/// MESMA transação: (1) a linha da avaliação de reconciliação vigente do escopo (onda/plano) — detectando
/// staleness (item 3) mesmo sob concorrência com <c>SqlReconciliationAssessmentStore.PersistAsync</c>/
/// <c>SqlReconciliationExceptionDispositionStore.SaveDecisionAsync</c> (todos usam <c>WITH (UPDLOCK,
/// HOLDLOCK)</c> sobre a mesma faixa de linhas, serializando as três operações); (2) as decisões vigentes
/// desta avaliação — recomputando <see cref="ReconciliationExceptionDecisionsStateHash"/> e recusando
/// fail-closed quando diverge do esperado pela Application (item 17/49: nunca um snapshot misto); e (3) os
/// certificates já existentes deste escopo, decidindo sob esse lock se o candidato converge para a versão
/// vigente (mesmo <see cref="ReconciliationCertificate.EvaluationFingerprint"/>, item 16 — replay idempotente)
/// ou se é uma versão realmente nova. Toda leitura revalida <see cref="ReconciliationCertificate.CertificateHash"/>
/// contra os campos REALMENTE persistidos (fronteira não confiável, mesmo princípio dos Passos 3/4). RLS por
/// SESSION_CONTEXT.
/// </summary>
public sealed class SqlReconciliationCertificateStore(TenantConnectionFactory connectionFactory) : IReconciliationCertificateStore
{
    private const string ResolveAttemptSequenceSql =
        "SELECT attempt_sequence FROM dbo.purview_import_job_plans WHERE wave_id = @wave AND project_id = @project AND planned_job_name = @name;";

    private const string ResolvePlannedJobNameSql =
        "SELECT planned_job_name FROM dbo.purview_import_job_plans WHERE wave_id = @wave AND project_id = @project AND attempt_sequence = @attempt;";

    private const string LatestAssessmentVersionForUpdateSql =
        """
        SELECT MAX(assessment_version) FROM dbo.purview_reconciliation_assessments WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project;
        """;

    // Colunas = wave_id(0), attempt_sequence(1), certificate_version(2), tenant_id(3), project_id(4),
    // assessment_version(5), assessment_source_fingerprint(6), mapping_fingerprint(7), result(8),
    // total_item_count(9), incomplete_item_count(10), deviation_count(11), deviations_sha256(12),
    // duplicate_risk_detected(13), evaluation_fingerprint(14), issued_by(15), issued_by_role(16),
    // correlation_id(17), generated_at_utc(18), schema_version(19), certificate_hash(20).
    private const string Columns =
        "wave_id, attempt_sequence, certificate_version, tenant_id, project_id, assessment_version, " +
        "assessment_source_fingerprint, mapping_fingerprint, result, total_item_count, incomplete_item_count, " +
        "deviation_count, deviations_sha256, duplicate_risk_detected, evaluation_fingerprint, issued_by, " +
        "issued_by_role, correlation_id, generated_at_utc, schema_version, certificate_hash";

    private const string LockedCertificatesSql =
        $"""
        SELECT {Columns} FROM dbo.purview_reconciliation_certificates WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY certificate_version DESC;
        """;

    private const string LatestCertificateSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.purview_reconciliation_certificates
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY certificate_version DESC;
        """;

    private const string CertificateByVersionSql =
        $"""
        SELECT {Columns} FROM dbo.purview_reconciliation_certificates
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND certificate_version = @version AND project_id = @project;
        """;

    private const string HistorySql =
        $"""
        SELECT {Columns} FROM dbo.purview_reconciliation_certificates
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY certificate_version ASC;
        """;

    private const string LatestAcrossOtherAttemptsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.purview_reconciliation_certificates
        WHERE wave_id = @wave AND project_id = @project AND attempt_sequence <> @attempt
        ORDER BY generated_at_utc DESC;
        """;

    // Mesma forma de ReconciliationExceptionDispositionStore.CurrentDecisionsForAssessmentSql, sob lock —
    // usada para recomputar ReconciliationExceptionDecisionsStateHash a partir das decisões REALMENTE
    // vigentes no instante da emissão (item 17/49).
    private const string LockedCurrentDecisionsSql =
        """
        WITH ranked AS
        (
            SELECT wave_id, attempt_sequence, assessment_version, item_kind, item_key, decision_version, tenant_id,
                   project_id, assessment_source_fingerprint, technical_disposition, status, reason_code,
                   reason_code_catalog_version, comment, decided_by, decided_by_role, correlation_id, decided_at_utc,
                   decision_fingerprint, decision_hash,
                   ROW_NUMBER() OVER (PARTITION BY item_kind, item_key ORDER BY decision_version DESC) AS rn
            FROM dbo.purview_reconciliation_exception_dispositions WITH (UPDLOCK, HOLDLOCK)
            WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version AND project_id = @project
        )
        SELECT wave_id, attempt_sequence, assessment_version, item_kind, item_key, decision_version, tenant_id,
               project_id, assessment_source_fingerprint, technical_disposition, status, reason_code,
               reason_code_catalog_version, comment, decided_by, decided_by_role, correlation_id, decided_at_utc,
               decision_fingerprint, decision_hash
        FROM ranked WHERE rn = 1;
        """;

    private const string InsertCertificateSql =
        $"""
        INSERT INTO dbo.purview_reconciliation_certificates ({Columns})
        VALUES
            (@wave, @attempt, @version, @tenant, @project, @assessmentVersion, @assessmentFingerprint,
             @mappingFingerprint, @result, @totalItemCount, @incompleteItemCount, @deviationCount,
             @deviationsSha256, @duplicateRisk, @evaluationFingerprint, @issuedBy, @issuedByRole, @correlation,
             @generatedAt, @schemaVersion, @hash);
        """;

    private const string InsertAuditEventSql =
        """
        INSERT INTO dbo.purview_reconciliation_certificate_audit_events
            (tenant_id, project_id, wave_id, planned_job_name, certificate_version, event_type, actor_id,
             actor_role, succeeded, reason, correlation_id, occurred_at_utc)
        VALUES
            (@tenant, @project, @wave, @plannedJobName, @certificateVersion, @eventType, @actorId, @actorRole,
             @succeeded, @reason, @correlation, @occurredAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<ReconciliationCertificate> IssueOrConvergeAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint,
        Sha256Hash expectedDecisionsStateFingerprint,
        ReconciliationOutcome result,
        int totalItemCount,
        int incompleteItemCount,
        int deviationCount,
        Sha256Hash deviationsSha256,
        bool duplicateRiskDetected,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidateFingerprint = ReconciliationCertificate.ComputeEvaluationFingerprint(assessmentSourceFingerprint, deviationsSha256, duplicateRiskDetected);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempt = await ResolveAttemptSequenceAsync(connection.Connection, transaction, scope, wave, plannedJobName, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new PurviewImportJobSourceNotFoundException("Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

            int? latestAssessmentVersion;
            await using (var command = new SqlCommand(LatestAssessmentVersionForUpdateSql, connection.Connection, transaction))
            {
                BindScope(command, wave, attempt, scope.Project);
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                latestAssessmentVersion = scalar is int value ? value : null;
            }

            if (latestAssessmentVersion != assessmentVersion)
            {
                throw new ReconciliationCertificateStaleChainException(
                    "A avaliação de reconciliação referenciada não é mais a vigente (foi superseded) — a emissão de " +
                    "certificate sobre a avaliação antiga é recusada (fail-closed).");
            }

            var lockedDecisions = new List<Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionDecision>();
            await using (var command = new SqlCommand(LockedCurrentDecisionsSql, connection.Connection, transaction))
            {
                BindVersionScope(command, wave, attempt, assessmentVersion, scope.Project);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    lockedDecisions.Add(ReadDecision(reader, plannedJobName));
                }
            }

            var actualDecisionsStateFingerprint = ReconciliationExceptionDecisionsStateHash.Compute(lockedDecisions);
            if (!string.Equals(actualDecisionsStateFingerprint.Value, expectedDecisionsStateFingerprint.Value, StringComparison.Ordinal))
            {
                throw new ReconciliationCertificateStaleChainException(
                    "As dispositions vigentes desta avaliação mudaram concorrentemente entre a resolução do candidato " +
                    "e a emissão — a emissão sobre o snapshot antigo é recusada (fail-closed).");
            }

            var currentVersion = 0;
            ReconciliationCertificate? current = null;
            await using (var command = new SqlCommand(LockedCertificatesSql, connection.Connection, transaction))
            {
                BindScope(command, wave, attempt, scope.Project);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    current = ReadCertificate(reader, plannedJobName);
                    currentVersion = current.CertificateVersion;
                }
            }

            if (current is not null && string.Equals(current.EvaluationFingerprint.Value, candidateFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico (item 16): converge sem inserir uma nova versão, mesmo sob concorrência
                // (item 11: N emissões concorrentes idênticas convergem todas para a MESMA versão vigente).
                await InsertAuditEventAsync(
                    connection.Connection, transaction, scope, wave, plannedJobName, current.CertificateVersion,
                    ReconciliationCertificateAuditEventType.Converged, issuedBy, issuedByRole, succeeded: true,
                    "Emissão convergiu idempotentemente para a versão vigente (replay idêntico).", correlation, now,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var nextVersion = currentVersion + 1;
            var certificate = ReconciliationCertificate.Create(
                scope.Tenant, scope.Project, wave, plannedJobName, nextVersion, assessmentVersion, assessmentSourceFingerprint,
                mappingFingerprint, result, totalItemCount, incompleteItemCount, deviationCount, deviationsSha256,
                duplicateRiskDetected, issuedBy, issuedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertCertificateSql, connection.Connection, transaction))
            {
                BindScope(command, wave, attempt, scope.Project);
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = certificate.CertificateVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@assessmentVersion", SqlDbType.Int) { Value = certificate.AssessmentVersion });
                command.Parameters.Add(new SqlParameter("@assessmentFingerprint", SqlDbType.Char, 64) { Value = certificate.AssessmentSourceFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@mappingFingerprint", SqlDbType.Char, 64) { Value = certificate.MappingFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@result", SqlDbType.TinyInt) { Value = (byte)certificate.Result });
                command.Parameters.Add(new SqlParameter("@totalItemCount", SqlDbType.Int) { Value = certificate.TotalItemCount });
                command.Parameters.Add(new SqlParameter("@incompleteItemCount", SqlDbType.Int) { Value = certificate.IncompleteItemCount });
                command.Parameters.Add(new SqlParameter("@deviationCount", SqlDbType.Int) { Value = certificate.DeviationCount });
                command.Parameters.Add(new SqlParameter("@deviationsSha256", SqlDbType.Char, 64) { Value = certificate.DeviationsSha256.Value });
                command.Parameters.Add(new SqlParameter("@duplicateRisk", SqlDbType.Bit) { Value = certificate.DuplicateRiskDetected });
                command.Parameters.Add(new SqlParameter("@evaluationFingerprint", SqlDbType.Char, 64) { Value = certificate.EvaluationFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@issuedBy", SqlDbType.NVarChar, 200) { Value = certificate.IssuedBy });
                command.Parameters.Add(new SqlParameter("@issuedByRole", SqlDbType.NVarChar, 50) { Value = certificate.IssuedByRole });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = certificate.Correlation.Value });
                command.Parameters.Add(new SqlParameter("@generatedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(certificate.GeneratedAtUtc) });
                command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = certificate.SchemaVersion });
                command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = certificate.CertificateHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertAuditEventAsync(
                connection.Connection, transaction, scope, wave, plannedJobName, certificate.CertificateVersion,
                ReconciliationCertificateAuditEventType.Issued, issuedBy, issuedByRole, succeeded: true,
                "Nova versão de reconciliation certificate emitida.", correlation, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return certificate;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ReconciliationCertificate?> GetLatestAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return null;
        }

        await using var command = new SqlCommand(LatestCertificateSql, connection.Connection);
        BindScope(command, wave, attempt.Value, scope.Project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCertificate(reader, plannedJobName) : null;
    }

    /// <inheritdoc />
    public async Task<ReconciliationCertificate?> GetByVersionAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int certificateVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return null;
        }

        await using var command = new SqlCommand(CertificateByVersionSql, connection.Connection);
        BindScope(command, wave, attempt.Value, scope.Project);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = certificateVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCertificate(reader, plannedJobName) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReconciliationCertificate>> GetHistoryAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return [];
        }

        var history = new List<ReconciliationCertificate>();
        await using var command = new SqlCommand(HistorySql, connection.Connection);
        BindScope(command, wave, attempt.Value, scope.Project);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(ReadCertificate(reader, plannedJobName));
        }

        return history;
    }

    /// <inheritdoc />
    public async Task<ReconciliationCertificate?> GetLatestForWaveAcrossOtherAttemptsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName excludingPlannedJobName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var excludingAttempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, excludingPlannedJobName, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException("Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        RawCertificateRow? rawRow;
        await using (var command = new SqlCommand(LatestAcrossOtherAttemptsSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = excludingAttempt });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            rawRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRawRow(reader) : null;
        }

        if (rawRow is not { } row)
        {
            return null;
        }

        // A linha pode pertencer a uma tentativa (PlannedJobName) DIFERENTE da conhecida pelo chamador — o
        // reader/command anterior já foi descartado antes desta segunda consulta (mesma conexão, sem MARS).
        var candidatePlannedJobName = await ResolvePlannedJobNameAsync(connection.Connection, scope, wave, row.AttemptSequence, cancellationToken).ConfigureAwait(false)
            ?? throw new ReconciliationCertificateIntegrityViolationException(
                "Não foi possível resolver o plano de import job da tentativa referenciada por um certificate já persistido (fail-closed).");

        return FromRaw(row, candidatePlannedJobName);
    }

    /// <inheritdoc />
    public async Task RecordAuditEventAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int? certificateVersion,
        ReconciliationCertificateAuditEventType eventType,
        string actorId,
        string actorRole,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertAuditEventSql, connection.Connection);
        BindAuditEventParameters(command, scope, wave, plannedJobName, certificateVersion, eventType, actorId, actorRole, succeeded, reason, correlation, occurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int? certificateVersion,
        ReconciliationCertificateAuditEventType eventType,
        string actorId,
        string actorRole,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(InsertAuditEventSql, connection, transaction);
        BindAuditEventParameters(command, scope, wave, plannedJobName, certificateVersion, eventType, actorId, actorRole, succeeded, reason, correlation, occurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindAuditEventParameters(
        SqlCommand command,
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int? certificateVersion,
        ReconciliationCertificateAuditEventType eventType,
        string actorId,
        string actorRole,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        DateTimeOffset occurredAtUtc)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@plannedJobName", SqlDbType.VarChar, 100) { Value = plannedJobName.Value });
        command.Parameters.Add(new SqlParameter("@certificateVersion", SqlDbType.Int) { Value = (object?)certificateVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@eventType", SqlDbType.TinyInt) { Value = (byte)eventType });
        command.Parameters.Add(new SqlParameter("@actorId", SqlDbType.NVarChar, 200) { Value = actorId });
        command.Parameters.Add(new SqlParameter("@actorRole", SqlDbType.NVarChar, 50) { Value = actorRole });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit) { Value = succeeded });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 500) { Value = reason });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
        command.Parameters.Add(new SqlParameter("@occurredAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(occurredAtUtc) });
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

    private static async Task<PurviewImportJobName?> ResolvePlannedJobNameAsync(
        SqlConnection connection, TenantScope scope, WaveId wave, int attempt, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(ResolvePlannedJobNameSql, connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is string value ? PurviewImportJobName.FromPersistedValue(value.TrimEnd()) : null;
    }

    private static void BindScope(SqlCommand command, WaveId wave, int attempt, ProjectId project)
    {
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
    }

    private static void BindVersionScope(SqlCommand command, WaveId wave, int attempt, int assessmentVersion, ProjectId project)
    {
        BindScope(command, wave, attempt, project);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessmentVersion });
    }

    /// <summary>
    /// Todos os campos de UMA linha da tabela de certificates, capturados ANTES de saber, em alguns
    /// caminhos de leitura (<see cref="GetLatestForWaveAcrossOtherAttemptsAsync"/>), qual
    /// <see cref="PurviewImportJobName"/> a linha pertence — permite fechar o <see cref="SqlDataReader"/>/
    /// <see cref="SqlCommand"/> antes de emitir uma segunda consulta na MESMA conexão (sem MARS).
    /// </summary>
    private sealed record RawCertificateRow(
        Guid Wave,
        int AttemptSequence,
        int CertificateVersion,
        Guid Tenant,
        Guid Project,
        int AssessmentVersion,
        string AssessmentSourceFingerprint,
        string MappingFingerprint,
        byte Result,
        int TotalItemCount,
        int IncompleteItemCount,
        int DeviationCount,
        string DeviationsSha256,
        bool DuplicateRiskDetected,
        string IssuedBy,
        string IssuedByRole,
        Guid Correlation,
        DateTime GeneratedAtUtc,
        string SchemaVersion,
        string CertificateHash);

    private static RawCertificateRow ReadRawRow(SqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetInt32(5),
            reader.GetString(6).TrimEnd(),
            reader.GetString(7).TrimEnd(),
            reader.GetByte(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12).TrimEnd(),
            reader.GetBoolean(13),
            reader.GetString(15).TrimEnd(),
            reader.GetString(16).TrimEnd(),
            reader.GetGuid(17),
            reader.GetDateTime(18),
            reader.GetString(19).TrimEnd(),
            reader.GetString(20).TrimEnd());

    private static ReconciliationCertificate FromRaw(RawCertificateRow row, PurviewImportJobName plannedJobName) =>
        ReconciliationCertificate.Rehydrate(
            new TenantId(row.Tenant),
            new ProjectId(row.Project),
            new WaveId(row.Wave),
            plannedJobName,
            row.CertificateVersion,
            row.AssessmentVersion,
            new Sha256Hash(row.AssessmentSourceFingerprint),
            new Sha256Hash(row.MappingFingerprint),
            (ReconciliationOutcome)row.Result,
            row.TotalItemCount,
            row.IncompleteItemCount,
            row.DeviationCount,
            new Sha256Hash(row.DeviationsSha256),
            row.DuplicateRiskDetected,
            row.IssuedBy,
            row.IssuedByRole,
            new CorrelationId(row.Correlation),
            SqlJobMapping.ReadUtc(row.GeneratedAtUtc),
            row.SchemaVersion,
            new Sha256Hash(row.CertificateHash));

    private static ReconciliationCertificate ReadCertificate(SqlDataReader reader, PurviewImportJobName plannedJobName) =>
        FromRaw(ReadRawRow(reader), plannedJobName);

    private static Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionDecision ReadDecision(SqlDataReader reader, PurviewImportJobName plannedJobName) =>
        Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionDecision.Rehydrate(
            new TenantId(reader.GetGuid(6)),
            new ProjectId(reader.GetGuid(7)),
            new WaveId(reader.GetGuid(0)),
            plannedJobName,
            reader.GetInt32(2),
            new Sha256Hash(reader.GetString(8).TrimEnd()),
            (Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionItemKind)reader.GetByte(3),
            reader.GetString(4).TrimEnd(),
            (Domain.TargetIngestion.Purview.Reconciliation.ReconciliationDisposition)reader.GetByte(9),
            reader.GetInt32(5),
            (Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionDecisionStatus)reader.GetByte(10),
            (Domain.TargetIngestion.Purview.Reconciliation.ReconciliationExceptionReasonCode)reader.GetByte(11),
            reader.GetByte(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetString(14).TrimEnd(),
            reader.GetString(15).TrimEnd(),
            new CorrelationId(reader.GetGuid(16)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(17)),
            new Sha256Hash(reader.GetString(18).TrimEnd()),
            new Sha256Hash(reader.GetString(19).TrimEnd()));
}
