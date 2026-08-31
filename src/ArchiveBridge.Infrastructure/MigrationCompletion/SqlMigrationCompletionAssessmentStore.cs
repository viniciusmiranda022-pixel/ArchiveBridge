using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.MigrationCompletion;

/// <summary>
/// Persistência da <see cref="MigrationCompletionAssessment"/> (AB-I8-010) — um header por versão
/// (<c>migration_completion_assessments</c>) mais uma linha por critério do catálogo dentro dessa versão
/// (<c>migration_completion_assessment_criterion_results</c>, mesmo padrão item-table de
/// <c>production_readiness_review_control_results</c>/0042). <see cref="RecordAssessmentAsync"/> locka, sob a
/// MESMA transação, o header já existente deste escopo e decide sob esse lock se o candidato converge para a
/// versão vigente (mesmo <see cref="MigrationCompletionAssessment.AssessmentFingerprint"/>, replay idempotente)
/// ou se é uma versão realmente nova. Toda leitura revalida <see cref="MigrationCompletionAssessment.AssessmentHash"/>
/// E reexecuta o avaliador puro sobre as linhas de critério carregadas (fronteira não confiável). RLS por
/// SESSION_CONTEXT.
/// </summary>
public sealed class SqlMigrationCompletionAssessmentStore(TenantConnectionFactory connectionFactory) : IMigrationCompletionAssessmentStore
{
    // Colunas do header = tenant_id(0), project_id(1), assessment_version(2), anchor_wave_id(3),
    // anchor_planned_job_name(4), outcome(5), assessment_fingerprint(6), submitted_by(7), submitted_by_role(8),
    // correlation_id(9), generated_at_utc(10), schema_version(11), assessment_hash(12).
    private const string HeaderColumns =
        "tenant_id, project_id, assessment_version, anchor_wave_id, anchor_planned_job_name, outcome, " +
        "assessment_fingerprint, submitted_by, submitted_by_role, correlation_id, generated_at_utc, schema_version, " +
        "assessment_hash";

    // Colunas das linhas de critério = tenant_id(0), project_id(1), assessment_version(2), criterion_id(3),
    // status(4), evidence_kind(5), evidence_fingerprint(6), evidence_locator(7), reason_code(8), observed_at_utc(9).
    private const string CriterionColumns =
        "tenant_id, project_id, assessment_version, criterion_id, status, evidence_kind, evidence_fingerprint, " +
        "evidence_locator, reason_code, observed_at_utc";

    private const string LockedHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.migration_completion_assessments WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY assessment_version DESC;
        """;

    private const string LatestHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.migration_completion_assessments
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY assessment_version DESC;
        """;

    private const string HistoryHeaderSql =
        $"""
        SELECT {HeaderColumns} FROM dbo.migration_completion_assessments
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY assessment_version ASC;
        """;

    private const string CriteriaForVersionSql =
        $"""
        SELECT {CriterionColumns} FROM dbo.migration_completion_assessment_criterion_results
        WHERE tenant_id = @tenant AND project_id = @project AND assessment_version = @version
        ORDER BY criterion_id ASC;
        """;

    private const string InsertHeaderSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @generatedAt);

        INSERT INTO dbo.migration_completion_assessments ({HeaderColumns})
        VALUES
            (@tenant, @project, @version, @anchorWaveId, @anchorPlannedJobName, @outcome, @assessmentFingerprint,
             @submittedBy, @submittedByRole, @correlation, @generatedAt, @schemaVersion, @assessmentHash);
        """;

    private const string InsertCriterionSql =
        $"""
        INSERT INTO dbo.migration_completion_assessment_criterion_results ({CriterionColumns})
        VALUES
            (@tenant, @project, @version, @criterionId, @status, @evidenceKind, @evidenceFingerprint,
             @evidenceLocator, @reasonCode, @observedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<MigrationCompletionAssessment> RecordAssessmentAsync(
        TenantScope scope,
        WaveId anchorWave,
        PurviewImportJobName anchorPlannedJobName,
        IReadOnlyDictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> resolvedCriterionResults,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = MigrationCompletionAssessment.Compose(
            scope.Tenant, scope.Project, assessmentVersion: 1, anchorWave, anchorPlannedJobName, resolvedCriterionResults,
            submittedBy, submittedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MigrationCompletionAssessment? current = null;
            HeaderRow? currentHeader = null;
            await using (var command = new SqlCommand(LockedHeaderSql, connection.Connection, transaction))
            {
                BindScope(command, scope);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    currentHeader = ReadHeader(reader);
                }
            }

            if (currentHeader is { } header)
            {
                var criteria = await ReadCriteriaAsync(connection.Connection, transaction, scope, header.AssessmentVersion, cancellationToken)
                    .ConfigureAwait(false);
                current = RehydrateAssessment(header, criteria);
            }

            if (current is not null
                && string.Equals(current.AssessmentFingerprint.Value, candidate.AssessmentFingerprint.Value, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var nextVersion = (current?.AssessmentVersion ?? 0) + 1;
            var record = MigrationCompletionAssessment.Compose(
                scope.Tenant, scope.Project, nextVersion, anchorWave, anchorPlannedJobName, resolvedCriterionResults,
                submittedBy, submittedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertHeaderSql, connection.Connection, transaction))
            {
                BindHeaderParameters(command, scope, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var criterionResult in record.CriterionResults)
            {
                await using var command = new SqlCommand(InsertCriterionSql, connection.Connection, transaction);
                BindCriterionParameters(command, scope, record.AssessmentVersion, criterionResult);
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
    public async Task<MigrationCompletionAssessment?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        HeaderRow? header;
        await using (var command = new SqlCommand(LatestHeaderSql, connection.Connection))
        {
            BindScope(command, scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            header = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadHeader(reader) : null;
        }

        if (header is not { } value)
        {
            return null;
        }

        var criteria = await ReadCriteriaAsync(connection.Connection, transaction: null, scope, value.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        return RehydrateAssessment(value, criteria);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationCompletionAssessment>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        var headers = new List<HeaderRow>();
        await using (var command = new SqlCommand(HistoryHeaderSql, connection.Connection))
        {
            BindScope(command, scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                headers.Add(ReadHeader(reader));
            }
        }

        var history = new List<MigrationCompletionAssessment>(headers.Count);
        foreach (var header in headers)
        {
            var criteria = await ReadCriteriaAsync(connection.Connection, transaction: null, scope, header.AssessmentVersion, cancellationToken)
                .ConfigureAwait(false);
            history.Add(RehydrateAssessment(header, criteria));
        }

        return history;
    }

    private static async Task<List<MigrationCompletionCriterionResult>> ReadCriteriaAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, int assessmentVersion, CancellationToken cancellationToken)
    {
        var criteria = new List<MigrationCompletionCriterionResult>();
        await using var command = transaction is null
            ? new SqlCommand(CriteriaForVersionSql, connection)
            : new SqlCommand(CriteriaForVersionSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessmentVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            criteria.Add(ReadCriterionResult(reader));
        }

        return criteria;
    }

    private static MigrationCompletionAssessment RehydrateAssessment(HeaderRow header, IReadOnlyList<MigrationCompletionCriterionResult> criteria) =>
        MigrationCompletionAssessment.Rehydrate(
            header.Tenant, header.Project, header.AssessmentVersion, header.AnchorWave, header.AnchorPlannedJobName, criteria,
            header.Outcome, header.AssessmentFingerprint, header.SubmittedBy, header.SubmittedByRole, header.Correlation,
            header.GeneratedAtUtc, header.SchemaVersion, header.AssessmentHash);

    private static void BindScope(SqlCommand command, TenantScope scope)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindHeaderParameters(SqlCommand command, TenantScope scope, MigrationCompletionAssessment record)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.AssessmentVersion });
        command.Parameters.Add(new SqlParameter("@anchorWaveId", SqlDbType.UniqueIdentifier) { Value = record.AnchorWave.Value });
        command.Parameters.Add(new SqlParameter("@anchorPlannedJobName", SqlDbType.NVarChar, PurviewImportJobName.MaxLength) { Value = record.AnchorPlannedJobName.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@assessmentFingerprint", SqlDbType.Char, 64) { Value = record.AssessmentFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@submittedBy", SqlDbType.NVarChar, 200) { Value = record.SubmittedBy });
        command.Parameters.Add(new SqlParameter("@submittedByRole", SqlDbType.NVarChar, 50) { Value = record.SubmittedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@generatedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.GeneratedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@assessmentHash", SqlDbType.Char, 64) { Value = record.AssessmentHash.Value });
    }

    private static void BindCriterionParameters(SqlCommand command, TenantScope scope, int assessmentVersion, MigrationCompletionCriterionResult result)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessmentVersion });
        command.Parameters.Add(new SqlParameter("@criterionId", SqlDbType.NVarChar, 80) { Value = result.CriterionId.Value });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)result.Status });
        command.Parameters.Add(new SqlParameter("@evidenceKind", SqlDbType.TinyInt) { Value = (byte)result.Evidence.Kind });
        command.Parameters.Add(new SqlParameter("@evidenceFingerprint", SqlDbType.Char, 64) { Value = result.Evidence.Fingerprint.Value });
        command.Parameters.Add(new SqlParameter("@evidenceLocator", SqlDbType.NVarChar, 300) { Value = result.Evidence.Locator });
        command.Parameters.Add(new SqlParameter("@reasonCode", SqlDbType.NVarChar, 200) { Value = result.ReasonCode });
        command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(result.ObservedAtUtc) });
    }

    private static HeaderRow ReadHeader(SqlDataReader reader) =>
        new(
            new TenantId(reader.GetGuid(0)),
            new ProjectId(reader.GetGuid(1)),
            reader.GetInt32(2),
            new WaveId(reader.GetGuid(3)),
            PurviewImportJobName.FromPersistedValue(reader.GetString(4).TrimEnd()),
            (MigrationCompletionOutcome)reader.GetByte(5),
            new Sha256Hash(reader.GetString(6).TrimEnd()),
            reader.GetString(7).TrimEnd(),
            reader.GetString(8).TrimEnd(),
            new CorrelationId(reader.GetGuid(9)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(10)),
            reader.GetString(11).TrimEnd(),
            new Sha256Hash(reader.GetString(12).TrimEnd()));

    private static MigrationCompletionCriterionResult ReadCriterionResult(SqlDataReader reader)
    {
        var criterionId = new MigrationCompletionCriterionId(reader.GetString(3).TrimEnd());
        var status = (ReadinessControlStatus)reader.GetByte(4);
        var evidenceKind = (ReadinessEvidenceKind)reader.GetByte(5);
        var evidenceFingerprint = new Sha256Hash(reader.GetString(6).TrimEnd());
        var evidenceLocator = reader.GetString(7).TrimEnd();
        var reasonCode = reader.GetString(8).TrimEnd();
        var observedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(9));

        var evidence = ReadinessEvidenceReference.Rehydrate(evidenceKind, evidenceFingerprint, evidenceLocator);
        return MigrationCompletionCriterionResult.Create(criterionId, status, evidence, reasonCode, observedAtUtc);
    }

    private sealed record HeaderRow(
        TenantId Tenant,
        ProjectId Project,
        int AssessmentVersion,
        WaveId AnchorWave,
        PurviewImportJobName AnchorPlannedJobName,
        MigrationCompletionOutcome Outcome,
        Sha256Hash AssessmentFingerprint,
        string SubmittedBy,
        string SubmittedByRole,
        CorrelationId Correlation,
        DateTimeOffset GeneratedAtUtc,
        string SchemaVersion,
        Sha256Hash AssessmentHash);
}
