using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Persistência das versões de mapping e da evidência (linhas + sha256 + impressão digital + caminho
/// do artefato). Uma nova geração nunca sobrescreve: dentro de uma transação (opcionalmente cercada
/// pelo Job), calcula N+1 sob lock, marca a versão utilizável anterior como Superseded, PUBLICA o
/// artefato imutável para a versão atribuída (antes do commit) e insere a nova versão como utilizável;
/// o índice único filtrado impõe no máximo uma utilizável por onda. A role da aplicação só pode
/// atualizar a coluna <c>status</c> (hashes/fingerprint/artefato imutáveis). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlMappingStore(TenantConnectionFactory connectionFactory, IClock clock) : IMappingStore
{
    private const string MaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.mapping_csv_versions WHERE wave_id = @waveId AND project_id = @project;";

    // Lê o MAX sob lock (UPDLOCK, HOLDLOCK) DENTRO da transação de gravação: sequência atômica de
    // versão, impedindo que duas gerações concorrentes atribuam o mesmo N+1 (TOCTOU).
    private const string LockedMaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.mapping_csv_versions WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE wave_id = @waveId AND project_id = @project;";

    private const string SupersedeSql =
        "UPDATE dbo.mapping_csv_versions SET status = 1 WHERE wave_id = @waveId AND project_id = @project AND status = 0;";

    private const string GetUsableSql =
        """
        SELECT wave_id, mapping_version, project_id, configuration_hash, selection_hash, content_sha256,
               row_count, validation_result, generated_by, created_at_utc, status, generation_fingerprint, artifact_path
        FROM dbo.mapping_csv_versions
        WHERE wave_id = @waveId AND project_id = @project AND status = 0;
        """;

    private const string InsertVersionSql =
        """
        INSERT INTO dbo.mapping_csv_versions
            (wave_id, mapping_version, tenant_id, project_id, configuration_hash, selection_hash,
             content_sha256, row_count, validation_result, generated_by, created_at_utc, status,
             generation_fingerprint, artifact_path, artifact_size_bytes)
        VALUES
            (@waveId, @version, @tenant, @project, @cfgHash, @selHash, @contentHash, @rowCount,
             @validation, @generatedBy, @createdAt, @status, @fingerprint, @artifactPath, @artifactSize);
        """;

    private const string InsertRowSql =
        """
        INSERT INTO dbo.mapping_csv_rows
            (wave_id, mapping_version, tenant_id, project_id, row_number, workload, file_path, name,
             mailbox, is_archive, target_root_folder, content_code_page)
        VALUES
            (@waveId, @version, @tenant, @project, @rowNumber, @workload, @filePath, @name,
             @mailbox, @isArchive, @target, @codePage);
        """;

    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<int> GetMaxVersionAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(MaxVersionSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int value ? value : 0;
    }

    /// <inheritdoc />
    public async Task<MappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(GetUsableSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MappingCsvVersion(
            new MappingVersion(reader.GetInt32(1)),
            new ProjectId(reader.GetGuid(2)),
            new WaveId(reader.GetGuid(0)),
            new Sha256Hash(reader.GetString(3).TrimEnd()),
            new Sha256Hash(reader.GetString(4).TrimEnd()),
            new Sha256Hash(reader.GetString(5).TrimEnd()),
            reader.GetInt32(6),
            (MappingValidationOutcome)reader.GetByte(7),
            reader.GetString(8),
            SqlJobMapping.ReadUtc(reader.GetDateTime(9)),
            (MappingVersionStatus)reader.GetByte(10),
            new MappingGenerationFingerprint(new Sha256Hash(reader.GetString(11).TrimEnd())),
            reader.GetString(12));
    }

    /// <inheritdoc />
    public async Task<MappingCsvVersion> SaveAsync(
        TenantScope scope,
        MappingGenerationResult result,
        JobFence? fence,
        Func<MappingVersion, CancellationToken, Task<MappingArtifactReference>> publishArtifactAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(publishArtifactAsync);
        var source = result.Version;
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Cercamento do Job (inerte quando não durável): valida na MESMA transação, com lock retido
            // (UPDLOCK/HOLDLOCK) até o commit e lease válido, que o efeito é do dono corrente.
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "Mapping", cancellationToken).ConfigureAwait(false);
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

            // Publica o artefato IMUTÁVEL da versão atribuída ANTES do commit: uma versão persistida
            // sempre tem artefato. Falha aqui aborta a transação (nenhuma versão sem artefato).
            var artifact = await publishArtifactAsync(mappingVersion, cancellationToken).ConfigureAwait(false);

            var persisted = source with { Version = mappingVersion, ArtifactPath = artifact.LogicalPath };

            await using (var command = new SqlCommand(SupersedeSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = persisted.Wave.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = new SqlCommand(InsertVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = persisted.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = persisted.Project.Value });
                command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = persisted.ConfigurationHash.Value });
                command.Parameters.Add(new SqlParameter("@selHash", SqlDbType.Char, 64) { Value = persisted.SelectionHash.Value });
                command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = persisted.ContentSha256.Value });
                command.Parameters.Add(new SqlParameter("@rowCount", SqlDbType.Int) { Value = persisted.RowCount });
                command.Parameters.Add(new SqlParameter("@validation", SqlDbType.TinyInt) { Value = (byte)persisted.Validation });
                command.Parameters.Add(new SqlParameter("@generatedBy", SqlDbType.NVarChar, 200) { Value = persisted.GeneratedBy });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(persisted.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)persisted.Status });
                command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = persisted.Fingerprint.Value.Value });
                command.Parameters.Add(new SqlParameter("@artifactPath", SqlDbType.NVarChar, 400) { Value = artifact.LogicalPath });
                command.Parameters.Add(new SqlParameter("@artifactSize", SqlDbType.BigInt) { Value = artifact.SizeBytes });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var rowNumber = 0;
            foreach (var row in result.Rows)
            {
                rowNumber++;
                await using var command = new SqlCommand(InsertRowSql, connection.Connection, transaction);
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = persisted.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = persisted.Project.Value });
                command.Parameters.Add(new SqlParameter("@rowNumber", SqlDbType.Int) { Value = rowNumber });
                command.Parameters.Add(new SqlParameter("@workload", SqlDbType.NVarChar, 50) { Value = MappingSchema.ExchangeWorkload });
                command.Parameters.Add(new SqlParameter("@filePath", SqlDbType.NVarChar, 400) { Value = row.FilePath });
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 260) { Value = row.Name });
                command.Parameters.Add(new SqlParameter("@mailbox", SqlDbType.NVarChar, 320) { Value = row.Mailbox });
                command.Parameters.Add(new SqlParameter("@isArchive", SqlDbType.NVarChar, 10) { Value = MappingSchema.ArchiveTrue });
                command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, 400) { Value = row.TargetRootFolder.Value });
                command.Parameters.Add(new SqlParameter("@codePage", SqlDbType.Int) { Value = row.ContentCodePage.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Revalida o cercamento (instante fresco) imediatamente antes do commit: um lease que
            // expirou durante a geração/publicação não confirma a versão.
            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return persisted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
