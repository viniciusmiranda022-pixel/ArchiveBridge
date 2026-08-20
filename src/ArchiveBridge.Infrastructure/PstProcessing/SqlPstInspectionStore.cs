using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Custódia SQL append-only das tentativas de inspeção (<see cref="PstInspectionRecord"/>). A idempotência
/// do resultado canônico é reforçada pelo índice único filtrado <c>UX_pst_inspections_canonical</c> — o
/// backstop de corrida quando duas execuções concorrentes tentam gravar o canônico do mesmo (tenant,
/// projeto, artefato, hash esperado) ao mesmo tempo (Application relê via <see cref="FindCanonicalAsync"/>
/// ao capturar <see cref="PstInspectionConflictException"/>).
/// </summary>
public sealed class SqlPstInspectionStore(TenantConnectionFactory connectionFactory) : IPstInspectionStore
{
    private const string FindCanonicalSql =
        """
        SET NOCOUNT ON;
        SELECT inspection_id, tenant_id, project_id, artifact_id, expected_hash, observed_hash,
               observed_size_bytes, outcome, diagnostic, format_variant, engine_name, engine_version,
               correlation_id, started_at_utc, completed_at_utc
        FROM dbo.pst_inspections
        WHERE outcome = 0 AND artifact_id = @artifact AND project_id = @project AND expected_hash = @expectedHash;
        """;

    private const string InsertSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.pst_inspections
            (inspection_id, tenant_id, project_id, artifact_id, expected_hash, observed_hash,
             observed_size_bytes, outcome, diagnostic, format_variant, engine_name, engine_version,
             correlation_id, started_at_utc, completed_at_utc)
        VALUES
            (@inspectionId, @tenant, @project, @artifact, @expectedHash, @observedHash,
             @observedSizeBytes, @outcome, @diagnostic, @formatVariant, @engineName, @engineVersion,
             @correlation, @startedAt, @completedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<PstInspectionRecord?> FindCanonicalAsync(
        TenantScope scope, ArtifactId artifact, Sha256Hash expectedHash, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(FindCanonicalSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = artifact.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@expectedHash", SqlDbType.Char, 64) { Value = expectedHash.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PstInspectionRecord> SaveAsync(PstInspectionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var scope = new TenantScope(record.Tenant, record.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, connection.Connection);
        Bind(command, record);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627)
        {
            throw new PstInspectionConflictException(
                "Uma tentativa canônica concorrente já foi gravada para este artefato/hash.", sql);
        }

        return record;
    }

    private static void Bind(SqlCommand command, PstInspectionRecord record)
    {
        command.Parameters.Add(new SqlParameter("@inspectionId", SqlDbType.UniqueIdentifier) { Value = record.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = record.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = record.Project.Value });
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = record.Artifact.Value });
        command.Parameters.Add(new SqlParameter("@expectedHash", SqlDbType.Char, 64) { Value = record.ExpectedHash.Value });
        command.Parameters.Add(new SqlParameter("@observedHash", SqlDbType.Char, 64)
        { Value = (object?)record.ObservedHash?.Value ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@observedSizeBytes", SqlDbType.BigInt)
        { Value = (object?)record.ObservedSizeBytes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@diagnostic", SqlDbType.TinyInt)
        { Value = record.Diagnostic is { } diagnostic ? (byte)diagnostic : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@formatVariant", SqlDbType.TinyInt)
        { Value = record.FormatVariant is { } variant ? (byte)variant : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@engineName", SqlDbType.NVarChar, 100) { Value = record.EngineName });
        command.Parameters.Add(new SqlParameter("@engineVersion", SqlDbType.NVarChar, 50) { Value = record.EngineVersion });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.CompletedAtUtc) });
    }

    private static PstInspectionRecord ReadRecord(SqlDataReader reader) =>
        PstInspectionRecord.Rehydrate(
            new InspectionId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new ArtifactId(reader.GetGuid(3)),
            new Sha256Hash(reader.GetString(4).TrimEnd()),
            reader.IsDBNull(5) ? null : new Sha256Hash(reader.GetString(5).TrimEnd()),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            (PstInspectionOutcome)reader.GetByte(7),
            reader.IsDBNull(8) ? null : (PstStructuralDiagnostic)reader.GetByte(8),
            reader.IsDBNull(9) ? null : (PstFormatVariant)reader.GetByte(9),
            reader.GetString(10),
            reader.GetString(11),
            new CorrelationId(reader.GetGuid(12)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(13)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(14)));
}
