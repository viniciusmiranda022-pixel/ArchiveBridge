using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Persistência das versões do mapping CSV do Purview e da sua evidência de METADADOS apenas (item 12 —
/// nunca o conteúdo das linhas, que vive somente no artefato imutável). Reaproveita o mesmo protocolo
/// recuperável em DUAS transações curtas de <see cref="ArchiveBridge.Infrastructure.Mapping.SqlMappingStore"/>
/// (item 8 — o padrão comprovado, não o schema, que diverge estruturalmente por não fixar
/// Workload/IsArchive/ContentCodePage): <see cref="ReserveAsync"/> (transação 1, sem I/O de filesystem) →
/// o chamador publica o artefato FORA do SQL → <see cref="FinalizeAsync"/> (transação 2). O índice único
/// filtrado impõe no máximo uma utilizável por onda. A role da aplicação só pode atualizar a coluna
/// <c>status</c> (hashes/fingerprint/artefato imutáveis). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlPurviewMappingCsvStore(TenantConnectionFactory connectionFactory, IClock clock) : IPurviewMappingCsvStore
{
    private const byte StatusUsable = (byte)MappingVersionStatus.Usable;
    private const byte StatusPendingArtifact = (byte)MappingVersionStatus.PendingArtifact;

    private const string MaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.purview_mapping_csv_versions WHERE wave_id = @waveId AND project_id = @project;";

    private const string LockedMaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.purview_mapping_csv_versions WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE wave_id = @waveId AND project_id = @project;";

    private const string SupersedeSql =
        "UPDATE dbo.purview_mapping_csv_versions SET status = 1 WHERE wave_id = @waveId AND project_id = @project AND status = 0;";

    private const string PromoteSql =
        "SET NOCOUNT OFF; UPDATE dbo.purview_mapping_csv_versions SET status = 0 " +
        "WHERE wave_id = @waveId AND project_id = @project AND mapping_version = @version AND status = 2;";

    private const string VersionColumns =
        "wave_id, mapping_version, project_id, evidence_fingerprint, content_sha256, row_count, " +
        "generated_by, created_at_utc, status, artifact_path";

    private const string GetUsableSql =
        $"SELECT {VersionColumns} FROM dbo.purview_mapping_csv_versions WHERE wave_id = @waveId AND project_id = @project AND status = 0;";

    private const string GetByVersionSql =
        $"SELECT {VersionColumns} FROM dbo.purview_mapping_csv_versions WHERE wave_id = @waveId AND project_id = @project AND mapping_version = @version;";

    private const string SelectByVersionSql =
        $"SELECT {VersionColumns} FROM dbo.purview_mapping_csv_versions WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE wave_id = @waveId AND project_id = @project AND mapping_version = @version;";

    private const string GetPendingByFingerprintSql =
        """
        SELECT TOP (1) mapping_version, artifact_path, evidence_fingerprint, content_sha256, artifact_size_bytes
        FROM dbo.purview_mapping_csv_versions
        WHERE wave_id = @waveId AND project_id = @project AND evidence_fingerprint = @fingerprint AND status = 2
        ORDER BY mapping_version ASC;
        """;

    private const string InsertVersionSql =
        """
        INSERT INTO dbo.purview_mapping_csv_versions
            (wave_id, mapping_version, tenant_id, project_id, evidence_fingerprint, content_sha256, row_count,
             generated_by, created_at_utc, status, artifact_path, artifact_size_bytes)
        VALUES
            (@waveId, @version, @tenant, @project, @fingerprint, @contentHash, @rowCount,
             @generatedBy, @createdAt, @status, @artifactPath, @artifactSize);
        """;

    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<int> GetMaxVersionAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(MaxVersionSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int value ? value : 0;
    }

    /// <inheritdoc />
    public async Task<PurviewMappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetUsableSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewMappingCsvVersion?> GetByVersionAsync(
        TenantScope scope, WaveId waveId, MappingVersion version, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetByVersionSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewMappingReservation?> GetPendingByFingerprintAsync(
        TenantScope scope, WaveId waveId, PurviewMappingGenerationFingerprint fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetPendingByFingerprintSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = fingerprint.Value.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PurviewMappingReservation(
            waveId,
            new MappingVersion(reader.GetInt32(0)),
            reader.GetString(1),
            new PurviewMappingGenerationFingerprint(new Sha256Hash(reader.GetString(2).TrimEnd())),
            new Sha256Hash(reader.GetString(3).TrimEnd()),
            reader.GetInt64(4));
    }

    /// <inheritdoc />
    public async Task<PurviewMappingReservation> ReserveAsync(
        TenantScope scope, PurviewMappingGenerationResult result, long expectedSizeBytes, JobFence? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var source = result.Evidence;
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "PurviewMapping", cancellationToken).ConfigureAwait(false);
            }

            int nextVersion;
            await using (var command = new SqlCommand(LockedMaxVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = source.Wave.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                nextVersion = (scalar is int value ? value : 0) + 1;
            }

            var mappingVersion = new MappingVersion(nextVersion);
            var logicalPath = new MappingArtifactDescriptor(scope, source.Wave, mappingVersion).LogicalPath;

            await using (var command = new SqlCommand(InsertVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = source.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = source.Project.Value });
                command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = source.Fingerprint.Value.Value });
                command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = source.ContentSha256.Value });
                command.Parameters.Add(new SqlParameter("@rowCount", SqlDbType.Int) { Value = source.RowCount });
                command.Parameters.Add(new SqlParameter("@generatedBy", SqlDbType.NVarChar, 200) { Value = source.GeneratedBy });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(source.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = StatusPendingArtifact });
                command.Parameters.Add(new SqlParameter("@artifactPath", SqlDbType.NVarChar, 400) { Value = logicalPath });
                command.Parameters.Add(new SqlParameter("@artifactSize", SqlDbType.BigInt) { Value = expectedSizeBytes });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new PurviewMappingReservation(source.Wave, mappingVersion, logicalPath, source.Fingerprint, source.ContentSha256, expectedSizeBytes);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PurviewMappingCsvVersion> FinalizeAsync(
        TenantScope scope,
        PurviewMappingReservation reservation,
        JobFence? fence,
        Func<CancellationToken, Task> validatePublishedArtifactAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(validatePublishedArtifactAsync);

        // Valida o artefato PUBLICADO ANTES de abrir a transação / adquirir qualquer lock.
        await validatePublishedArtifactAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "PurviewMapping", cancellationToken).ConfigureAwait(false);
            }

            var current = await ReadByVersionAsync(connection.Connection, transaction, reservation.Wave, reservation.Version, scope, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PurviewMappingCsvGenerationException("Reserva de versão de mapping do Purview ausente na finalização (fail-closed).");

            if (current.Fingerprint != reservation.Fingerprint
                || !string.Equals(current.ContentSha256.Value, reservation.ContentSha256.Value, StringComparison.Ordinal))
            {
                throw new PurviewMappingCsvGenerationException(
                    "Versão reservada não corresponde à impressão digital/hash da finalização (fail-closed).");
            }

            if (current.Status is MappingVersionStatus.Usable or MappingVersionStatus.Superseded)
            {
                // Já finalizada por uma execução anterior (replay idempotente): nenhum novo efeito.
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            await using (var supersede = new SqlCommand(SupersedeSql, connection.Connection, transaction))
            {
                supersede.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = reservation.Wave.Value });
                supersede.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int promoted;
            await using (var promote = new SqlCommand(PromoteSql, connection.Connection, transaction))
            {
                promote.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = current.Wave.Value });
                promote.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                promote.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reservation.Version.Value });
                promoted = await promote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (promoted != 1)
            {
                throw new PurviewMappingCsvGenerationException("Falha ao promover a reserva de mapping do Purview a utilizável (fail-closed).");
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return current with { Status = MappingVersionStatus.Usable };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<PurviewMappingCsvVersion?> ReadByVersionAsync(
        SqlConnection connection, SqlTransaction transaction, WaveId waveId, MappingVersion version, TenantScope scope, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SelectByVersionSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    // VersionColumns = wave_id(0), mapping_version(1), project_id(2), evidence_fingerprint(3),
    // content_sha256(4), row_count(5), generated_by(6), created_at_utc(7), status(8), artifact_path(9).
    private static PurviewMappingCsvVersion ReadVersion(SqlDataReader reader) =>
        new(
            new MappingVersion(reader.GetInt32(1)),
            new ProjectId(reader.GetGuid(2)),
            new WaveId(reader.GetGuid(0)),
            new Sha256Hash(reader.GetString(4).TrimEnd()),
            reader.GetInt32(5),
            reader.GetString(6),
            SqlJobMapping.ReadUtc(reader.GetDateTime(7)),
            (MappingVersionStatus)reader.GetByte(8),
            new PurviewMappingGenerationFingerprint(new Sha256Hash(reader.GetString(3).TrimEnd())),
            reader.GetString(9));
}
