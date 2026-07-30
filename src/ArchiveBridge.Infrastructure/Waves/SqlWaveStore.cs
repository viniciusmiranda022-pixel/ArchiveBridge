using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Waves;

/// <summary>
/// Persistência do agregado <see cref="MigrationWave"/> em SQL Server: a onda, o histórico imutável
/// de versões de seleção (<c>wave_versions</c>) e as entradas por versão (<c>wave_entries</c>,
/// metadados de PST — nunca conteúdo). A transição de estado grava apenas status/aprovação; a nova
/// seleção grava uma nova versão + entradas e devolve a onda a Draft. Congelamento pós-aprovação é
/// reforçado por gatilho. RLS por SESSION_CONTEXT + filtro por project_id.
/// </summary>
public sealed class SqlWaveStore(TenantConnectionFactory connectionFactory) : IWaveStore
{
    private const string InsertWaveSql =
        """
        INSERT INTO dbo.migration_waves
            (wave_id, tenant_id, project_id, wave_version, name, status, target_root_folder,
             planned_bytes, planned_items, configuration_hash, selection_hash,
             approved_at_utc, approved_by, created_at_utc)
        VALUES
            (@waveId, @tenant, @project, @version, @name, @status, @target,
             @plannedBytes, @plannedItems, @cfgHash, @selHash, @approvedAt, @approvedBy, @createdAt);
        """;

    private const string InsertVersionSql =
        """
        INSERT INTO dbo.wave_versions
            (wave_id, tenant_id, project_id, wave_version, selection_hash, configuration_hash,
             target_root_folder, planned_bytes, planned_items, created_at_utc)
        VALUES
            (@waveId, @tenant, @project, @version, @selHash, @cfgHash, @target, @plannedBytes, @plannedItems, @createdAt);
        """;

    private const string InsertEntrySql =
        """
        INSERT INTO dbo.wave_entries
            (wave_id, tenant_id, project_id, wave_version, file_path, pst_name, mailbox, size_bytes, item_count)
        VALUES
            (@waveId, @tenant, @project, @version, @filePath, @pstName, @mailbox, @sizeBytes, @itemCount);
        """;

    private const string GetWaveSql =
        """
        SELECT wave_id, tenant_id, project_id, wave_version, name, status, target_root_folder,
               configuration_hash, approved_at_utc, approved_by, created_at_utc
        FROM dbo.migration_waves
        WHERE wave_id = @waveId AND project_id = @project;
        """;

    private const string GetEntriesSql =
        """
        SELECT file_path, pst_name, mailbox, size_bytes, item_count
        FROM dbo.wave_entries
        WHERE wave_id = @waveId AND wave_version = @version
        ORDER BY entry_id;
        """;

    private const string SaveStatusSql =
        """
        UPDATE dbo.migration_waves
        SET status = @status, approved_at_utc = @approvedAt, approved_by = @approvedBy
        WHERE wave_id = @waveId AND project_id = @project;
        """;

    private const string SaveSelectionUpdateSql =
        """
        UPDATE dbo.migration_waves
        SET wave_version = @version, status = @status, selection_hash = @selHash,
            configuration_hash = @cfgHash, target_root_folder = @target,
            planned_bytes = @plannedBytes, planned_items = @plannedItems,
            approved_at_utc = NULL, approved_by = NULL
        WHERE wave_id = @waveId AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AddAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wave);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = new SqlCommand(InsertWaveSql, connection.Connection, transaction))
            {
                BindWaveHeader(command, wave);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertVersionAndEntriesAsync(connection.Connection, transaction, wave, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MigrationWave?> GetAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        WaveHeader header;
        await using (var command = new SqlCommand(GetWaveSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            header = ReadWaveHeader(reader);
        }

        var entries = new List<WaveEntry>();
        await using (var command = new SqlCommand(GetEntriesSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = header.Version.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new WaveEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    new ArchiveRef(reader.GetString(2)),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }
        }

        return MigrationWave.Rehydrate(
            header.Id,
            header.Tenant,
            header.Project,
            header.Name,
            header.TargetRootFolder,
            header.ConfigurationHash,
            new WaveSelection(entries),
            header.Version,
            header.Status,
            header.ApprovedAtUtc,
            header.ApprovedBy,
            header.CreatedAtUtc);
    }

    /// <inheritdoc />
    public async Task SaveStatusAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wave);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(SaveStatusSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)wave.Status });
        command.Parameters.Add(new SqlParameter("@approvedAt", SqlDbType.DateTime2)
        { Value = wave.ApprovedAtUtc is { } at ? SqlJobMapping.ToDbUtc(at) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@approvedBy", SqlDbType.NVarChar, 200)
        { Value = (object?)wave.ApprovedBy ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wave);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = new SqlCommand(SaveSelectionUpdateSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = wave.Version.Value });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)wave.Status });
                command.Parameters.Add(new SqlParameter("@selHash", SqlDbType.Char, 64) { Value = wave.SelectionHash.Value });
                command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = wave.ConfigurationHash.Value });
                command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, 400) { Value = wave.TargetRootFolder.Value });
                command.Parameters.Add(new SqlParameter("@plannedBytes", SqlDbType.BigInt) { Value = wave.PlannedBytes });
                command.Parameters.Add(new SqlParameter("@plannedItems", SqlDbType.BigInt) { Value = wave.PlannedItems });
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertVersionAndEntriesAsync(connection.Connection, transaction, wave, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task InsertVersionAndEntriesAsync(
        SqlConnection connection, SqlTransaction transaction, MigrationWave wave, CancellationToken cancellationToken)
    {
        await using (var command = new SqlCommand(InsertVersionSql, connection, transaction))
        {
            command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = wave.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = wave.Version.Value });
            command.Parameters.Add(new SqlParameter("@selHash", SqlDbType.Char, 64) { Value = wave.SelectionHash.Value });
            command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = wave.ConfigurationHash.Value });
            command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, 400) { Value = wave.TargetRootFolder.Value });
            command.Parameters.Add(new SqlParameter("@plannedBytes", SqlDbType.BigInt) { Value = wave.PlannedBytes });
            command.Parameters.Add(new SqlParameter("@plannedItems", SqlDbType.BigInt) { Value = wave.PlannedItems });
            command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(wave.CreatedAtUtc) });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in wave.Selection.Entries)
        {
            await using var command = new SqlCommand(InsertEntrySql, connection, transaction);
            command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = wave.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = wave.Version.Value });
            command.Parameters.Add(new SqlParameter("@filePath", SqlDbType.NVarChar, 400) { Value = entry.FilePath });
            command.Parameters.Add(new SqlParameter("@pstName", SqlDbType.NVarChar, 260) { Value = entry.PstName });
            command.Parameters.Add(new SqlParameter("@mailbox", SqlDbType.NVarChar, 320) { Value = entry.Archive.Mailbox });
            command.Parameters.Add(new SqlParameter("@sizeBytes", SqlDbType.BigInt) { Value = entry.SizeBytes });
            command.Parameters.Add(new SqlParameter("@itemCount", SqlDbType.BigInt) { Value = entry.ItemCount });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void BindWaveHeader(SqlCommand command, MigrationWave wave)
    {
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = wave.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = wave.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = wave.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = wave.Version.Value });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = wave.Name.Value });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)wave.Status });
        command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, 400) { Value = wave.TargetRootFolder.Value });
        command.Parameters.Add(new SqlParameter("@plannedBytes", SqlDbType.BigInt) { Value = wave.PlannedBytes });
        command.Parameters.Add(new SqlParameter("@plannedItems", SqlDbType.BigInt) { Value = wave.PlannedItems });
        command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = wave.ConfigurationHash.Value });
        command.Parameters.Add(new SqlParameter("@selHash", SqlDbType.Char, 64) { Value = wave.SelectionHash.Value });
        command.Parameters.Add(new SqlParameter("@approvedAt", SqlDbType.DateTime2)
        { Value = wave.ApprovedAtUtc is { } at ? SqlJobMapping.ToDbUtc(at) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@approvedBy", SqlDbType.NVarChar, 200)
        { Value = (object?)wave.ApprovedBy ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(wave.CreatedAtUtc) });
    }

    private static WaveHeader ReadWaveHeader(SqlDataReader reader) =>
        new(
            new WaveId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new WaveVersion(reader.GetInt32(3)),
            new WaveName(reader.GetString(4)),
            (WaveStatus)reader.GetByte(5),
            new TargetRootFolder(reader.GetString(6)),
            new Sha256Hash(reader.GetString(7).TrimEnd()),
            reader.IsDBNull(8) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            SqlJobMapping.ReadUtc(reader.GetDateTime(10)));

    private readonly record struct WaveHeader(
        WaveId Id,
        TenantId Tenant,
        ProjectId Project,
        WaveVersion Version,
        WaveName Name,
        WaveStatus Status,
        TargetRootFolder TargetRootFolder,
        Sha256Hash ConfigurationHash,
        DateTimeOffset? ApprovedAtUtc,
        string? ApprovedBy,
        DateTimeOffset CreatedAtUtc);
}
