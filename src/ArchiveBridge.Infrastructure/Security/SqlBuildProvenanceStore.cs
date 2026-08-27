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

/// <summary>Persistência do <see cref="BuildProvenanceRecord"/> (AB-I7-008). Mesmo padrão de lock/convergência/revalidação de integridade das demais stores deste Passo.</summary>
public sealed class SqlBuildProvenanceStore(TenantConnectionFactory connectionFactory) : IBuildProvenanceStore
{
    // Colunas = tenant_id(0), project_id(1), artifact_name(2), artifact_version(3), source_commit_sha(4),
    // builder_identity(5), build_timestamp_utc(6), artifact_digest(7), content_fingerprint(8),
    // approved_by(9), approved_by_role(10), correlation_id(11), approved_at_utc(12), schema_version(13),
    // record_hash(14).
    private const string Columns =
        "tenant_id, project_id, artifact_name, artifact_version, source_commit_sha, builder_identity, " +
        "build_timestamp_utc, artifact_digest, content_fingerprint, approved_by, approved_by_role, correlation_id, " +
        "approved_at_utc, schema_version, record_hash";

    private const string LockedRecordsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_build_provenance WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND artifact_name = @artifactName
        ORDER BY artifact_version DESC;
        """;

    private const string LatestSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_build_provenance
        WHERE tenant_id = @tenant AND project_id = @project AND artifact_name = @artifactName
        ORDER BY artifact_version DESC;
        """;

    private const string InsertSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @approvedAt);

        INSERT INTO dbo.security_build_provenance ({Columns})
        VALUES
            (@tenant, @project, @artifactName, @version, @commitSha, @builderIdentity, @buildTimestamp, @artifactDigest,
             @contentFingerprint, @approvedBy, @approvedByRole, @correlation, @approvedAt, @schemaVersion, @recordHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<BuildProvenanceRecord> ApproveAsync(
        TenantScope scope,
        string artifactName,
        string sourceCommitSha,
        string builderIdentity,
        DateTimeOffset buildTimestampUtc,
        Sha256Hash artifactDigest,
        string approvedBy,
        string approvedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = BuildProvenanceRecord.Approve(
            scope.Tenant, scope.Project, artifactName, artifactVersion: 1, sourceCommitSha, builderIdentity,
            buildTimestampUtc, artifactDigest, approvedBy, approvedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BuildProvenanceRecord? current = null;
            await using (var command = new SqlCommand(LockedRecordsSql, connection.Connection, transaction))
            {
                BindScope(command, scope, candidate.ArtifactName);
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

            var nextVersion = (current?.ArtifactVersion ?? 0) + 1;
            var record = BuildProvenanceRecord.Approve(
                scope.Tenant, scope.Project, artifactName, nextVersion, sourceCommitSha, builderIdentity,
                buildTimestampUtc, artifactDigest, approvedBy, approvedByRole, correlation, now);

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
    public async Task<BuildProvenanceRecord?> GetLatestAsync(TenantScope scope, string artifactName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestSql, connection.Connection);
        BindScope(command, scope, artifactName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    private static void BindScope(SqlCommand command, TenantScope scope, string artifactName)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@artifactName", SqlDbType.NVarChar, 200) { Value = artifactName });
    }

    private static void BindRecordParameters(SqlCommand command, TenantScope scope, BuildProvenanceRecord record)
    {
        BindScope(command, scope, record.ArtifactName);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.ArtifactVersion });
        command.Parameters.Add(new SqlParameter("@commitSha", SqlDbType.Char, 40) { Value = record.SourceCommitSha });
        command.Parameters.Add(new SqlParameter("@builderIdentity", SqlDbType.NVarChar, 200) { Value = record.BuilderIdentity });
        command.Parameters.Add(new SqlParameter("@buildTimestamp", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.BuildTimestampUtc) });
        command.Parameters.Add(new SqlParameter("@artifactDigest", SqlDbType.Char, 64) { Value = record.ArtifactDigest.Value });
        command.Parameters.Add(new SqlParameter("@contentFingerprint", SqlDbType.Char, 64) { Value = record.ContentFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@approvedBy", SqlDbType.NVarChar, 200) { Value = record.ApprovedBy });
        command.Parameters.Add(new SqlParameter("@approvedByRole", SqlDbType.NVarChar, 50) { Value = record.ApprovedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@approvedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.ApprovedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = record.RecordHash.Value });
    }

    private static BuildProvenanceRecord ReadRecord(SqlDataReader reader)
    {
        var tenant = new TenantId(reader.GetGuid(0));
        var project = new ProjectId(reader.GetGuid(1));
        var artifactName = reader.GetString(2).TrimEnd();
        var artifactVersion = reader.GetInt32(3);
        var sourceCommitSha = reader.GetString(4).TrimEnd();
        var builderIdentity = reader.GetString(5).TrimEnd();
        var buildTimestampUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(6));
        var artifactDigest = new Sha256Hash(reader.GetString(7).TrimEnd());
        var contentFingerprint = new Sha256Hash(reader.GetString(8).TrimEnd());
        var approvedBy = reader.GetString(9).TrimEnd();
        var approvedByRole = reader.GetString(10).TrimEnd();
        var correlation = new CorrelationId(reader.GetGuid(11));
        var approvedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(12));
        var schemaVersion = reader.GetString(13).TrimEnd();
        var recordHash = new Sha256Hash(reader.GetString(14).TrimEnd());

        return BuildProvenanceRecord.Rehydrate(
            tenant, project, artifactName, artifactVersion, sourceCommitSha, builderIdentity, buildTimestampUtc,
            artifactDigest, approvedBy, approvedByRole, correlation, approvedAtUtc, schemaVersion, contentFingerprint,
            recordHash);
    }
}
