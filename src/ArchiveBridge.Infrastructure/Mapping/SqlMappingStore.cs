using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Persistência das versões de mapping e da evidência (linhas + sha256). Uma nova geração nunca
/// sobrescreve: dentro de uma transação, marca a versão utilizável anterior como Superseded e insere
/// a nova como utilizável; o índice único filtrado impõe no máximo uma utilizável por onda. A role
/// da aplicação só pode atualizar a coluna <c>status</c> (hashes imutáveis). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlMappingStore(TenantConnectionFactory connectionFactory) : IMappingStore
{
    private const string MaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.mapping_csv_versions WHERE wave_id = @waveId AND project_id = @project;";

    private const string SupersedeSql =
        "UPDATE dbo.mapping_csv_versions SET status = 1 WHERE wave_id = @waveId AND project_id = @project AND status = 0;";

    private const string InsertVersionSql =
        """
        INSERT INTO dbo.mapping_csv_versions
            (wave_id, mapping_version, tenant_id, project_id, configuration_hash, selection_hash,
             content_sha256, row_count, validation_result, generated_by, created_at_utc, status)
        VALUES
            (@waveId, @version, @tenant, @project, @cfgHash, @selHash, @contentHash, @rowCount,
             @validation, @generatedBy, @createdAt, @status);
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

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

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
    public async Task SaveAsync(TenantScope scope, MappingGenerationResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var version = result.Version;
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = new SqlCommand(SupersedeSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = version.Wave.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = new SqlCommand(InsertVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = version.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version.Version.Value });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = version.Project.Value });
                command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = version.ConfigurationHash.Value });
                command.Parameters.Add(new SqlParameter("@selHash", SqlDbType.Char, 64) { Value = version.SelectionHash.Value });
                command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = version.ContentSha256.Value });
                command.Parameters.Add(new SqlParameter("@rowCount", SqlDbType.Int) { Value = version.RowCount });
                command.Parameters.Add(new SqlParameter("@validation", SqlDbType.TinyInt) { Value = (byte)version.Validation });
                command.Parameters.Add(new SqlParameter("@generatedBy", SqlDbType.NVarChar, 200) { Value = version.GeneratedBy });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(version.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)version.Status });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var rowNumber = 0;
            foreach (var row in result.Rows)
            {
                rowNumber++;
                await using var command = new SqlCommand(InsertRowSql, connection.Connection, transaction);
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = version.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version.Version.Value });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = version.Project.Value });
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

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
