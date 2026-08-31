using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.MigrationCompletion;

/// <summary>
/// Persistência do <see cref="MigrationCompletionCriterionAttestation"/> (AB-I8-010). <see cref="RecordAttestationAsync"/>
/// locka, sob a MESMA transação, os registros já existentes deste escopo (tenant/project/critério) e decide
/// sob esse lock se o candidato converge para a versão vigente (mesmo <see cref="MigrationCompletionCriterionAttestation.ContentFingerprint"/>,
/// replay idempotente) ou se é uma versão realmente nova. Toda leitura revalida
/// <see cref="MigrationCompletionCriterionAttestation.RecordHash"/> (fronteira não confiável). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlMigrationCompletionCriterionAttestationStore(TenantConnectionFactory connectionFactory) : IMigrationCompletionCriterionAttestationStore
{
    // Colunas = tenant_id(0), project_id(1), criterion_id(2), attestation_version(3), status(4),
    // evidence_kind(5), evidence_fingerprint(6), evidence_locator(7), reason_code(8), content_fingerprint(9),
    // submitted_by(10), submitted_by_role(11), correlation_id(12), submitted_at_utc(13), schema_version(14),
    // record_hash(15).
    private const string Columns =
        "tenant_id, project_id, criterion_id, attestation_version, status, evidence_kind, evidence_fingerprint, " +
        "evidence_locator, reason_code, content_fingerprint, submitted_by, submitted_by_role, correlation_id, " +
        "submitted_at_utc, schema_version, record_hash";

    private const string LockedRecordsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.migration_completion_criterion_attestations WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND criterion_id = @criterionId
        ORDER BY attestation_version DESC;
        """;

    private const string LatestSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.migration_completion_criterion_attestations
        WHERE tenant_id = @tenant AND project_id = @project AND criterion_id = @criterionId
        ORDER BY attestation_version DESC;
        """;

    private const string LatestForAllSql =
        $"""
        SELECT {Columns} FROM
        (
            SELECT {Columns}, ROW_NUMBER() OVER (PARTITION BY criterion_id ORDER BY attestation_version DESC) AS rn
            FROM dbo.migration_completion_criterion_attestations
            WHERE tenant_id = @tenant AND project_id = @project
        ) ranked
        WHERE rn = 1
        ORDER BY criterion_id ASC;
        """;

    private const string InsertSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @submittedAt);

        INSERT INTO dbo.migration_completion_criterion_attestations ({Columns})
        VALUES
            (@tenant, @project, @criterionId, @version, @status, @evidenceKind, @evidenceFingerprint, @evidenceLocator,
             @reasonCode, @contentFingerprint, @submittedBy, @submittedByRole, @correlation, @submittedAt,
             @schemaVersion, @recordHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<MigrationCompletionCriterionAttestation> RecordAttestationAsync(
        TenantScope scope,
        MigrationCompletionCriterionId criterionId,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = MigrationCompletionCriterionAttestation.Create(
            scope.Tenant, scope.Project, criterionId, attestationVersion: 1, status, evidence, reasonCode, submittedBy,
            submittedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MigrationCompletionCriterionAttestation? current = null;
            await using (var command = new SqlCommand(LockedRecordsSql, connection.Connection, transaction))
            {
                BindScope(command, scope, criterionId);
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

            var nextVersion = (current?.AttestationVersion ?? 0) + 1;
            var record = MigrationCompletionCriterionAttestation.Create(
                scope.Tenant, scope.Project, criterionId, nextVersion, status, evidence, reasonCode, submittedBy,
                submittedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertSql, connection.Connection, transaction))
            {
                BindRecordParameters(command, scope, record);
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
    public async Task<MigrationCompletionCriterionAttestation?> GetLatestAsync(
        TenantScope scope, MigrationCompletionCriterionId criterionId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestSql, connection.Connection);
        BindScope(command, scope, criterionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationCompletionCriterionAttestation>> GetLatestForAllAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var results = new List<MigrationCompletionCriterionAttestation>();
        await using var command = new SqlCommand(LatestForAllSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    private static void BindScope(SqlCommand command, TenantScope scope, MigrationCompletionCriterionId criterionId)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@criterionId", SqlDbType.NVarChar, 80) { Value = criterionId.Value });
    }

    private static void BindRecordParameters(SqlCommand command, TenantScope scope, MigrationCompletionCriterionAttestation record)
    {
        BindScope(command, scope, record.CriterionId);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.AttestationVersion });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)record.Status });
        command.Parameters.Add(new SqlParameter("@evidenceKind", SqlDbType.TinyInt) { Value = (byte)record.Evidence.Kind });
        command.Parameters.Add(new SqlParameter("@evidenceFingerprint", SqlDbType.Char, 64) { Value = record.Evidence.Fingerprint.Value });
        command.Parameters.Add(new SqlParameter("@evidenceLocator", SqlDbType.NVarChar, 300) { Value = record.Evidence.Locator });
        command.Parameters.Add(new SqlParameter("@reasonCode", SqlDbType.NVarChar, 200) { Value = record.ReasonCode });
        command.Parameters.Add(new SqlParameter("@contentFingerprint", SqlDbType.Char, 64) { Value = record.ContentFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@submittedBy", SqlDbType.NVarChar, 200) { Value = record.SubmittedBy });
        command.Parameters.Add(new SqlParameter("@submittedByRole", SqlDbType.NVarChar, 50) { Value = record.SubmittedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@submittedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.SubmittedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = record.RecordHash.Value });
    }

    private static MigrationCompletionCriterionAttestation ReadRecord(SqlDataReader reader)
    {
        var tenant = new TenantId(reader.GetGuid(0));
        var project = new ProjectId(reader.GetGuid(1));
        var criterionId = new MigrationCompletionCriterionId(reader.GetString(2).TrimEnd());
        var attestationVersion = reader.GetInt32(3);
        var status = (ReadinessControlStatus)reader.GetByte(4);
        var evidenceKind = (ReadinessEvidenceKind)reader.GetByte(5);
        var evidenceFingerprint = new Sha256Hash(reader.GetString(6).TrimEnd());
        var evidenceLocator = reader.GetString(7).TrimEnd();
        var reasonCode = reader.GetString(8).TrimEnd();
        var contentFingerprint = new Sha256Hash(reader.GetString(9).TrimEnd());
        var submittedBy = reader.GetString(10).TrimEnd();
        var submittedByRole = reader.GetString(11).TrimEnd();
        var correlation = new CorrelationId(reader.GetGuid(12));
        var submittedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(13));
        var schemaVersion = reader.GetString(14).TrimEnd();
        var recordHash = new Sha256Hash(reader.GetString(15).TrimEnd());

        var evidence = ReadinessEvidenceReference.Rehydrate(evidenceKind, evidenceFingerprint, evidenceLocator);

        return MigrationCompletionCriterionAttestation.Rehydrate(
            tenant, project, criterionId, attestationVersion, status, evidence, reasonCode, submittedBy, submittedByRole,
            correlation, submittedAtUtc, schemaVersion, contentFingerprint, recordHash);
    }
}
