using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Persistência dos snapshots de estatísticas de archive EXO e das estatísticas de pasta filhas
/// (AB-I6-005 itens 11-12). <see cref="PersistAsync"/> locka TODAS as versões existentes do escopo
/// (tenant/projeto/onda/archive/fase) sob a MESMA transação e decide, sob esse lock, tanto a próxima
/// <c>snapshot_version</c> QUANTO se alguma versão já existente converge pelo MESMO
/// <c>observation_hash</c> — chamadas concorrentes com conteúdo lógico idêntico sempre convergem para a
/// MESMA versão, nunca alocam versões duplicadas (mesmo padrão de
/// <c>SqlPurviewServiceResultReportStore.PersistAsync</c>, AB-I6-003 Blocker 3) — e insere, na MESMA
/// transação curta, o header e as estatísticas de pasta filhas quando não há convergência (nunca em
/// transações separadas — nenhuma versão "parcial" é jamais visível). Toda leitura que trata uma versão
/// persistida como evidência canônica (latest, versão específica, ou o ramo de convergência concorrente de
/// <see cref="PersistAsync"/>) revalida os hashes REALMENTE persistidos: <see cref="ExoArchiveStatisticsSnapshot.Rehydrate"/>
/// recomputa <c>observation_hash</c>/<c>snapshot_hash</c> a partir dos campos do header, e as estatísticas
/// de pasta filhas realmente carregadas são recontadas e rehashadas contra <c>folder_count</c>/
/// <c>folders_sha256</c> (mesmo princípio de AB-I6-004) — nenhum caminho pode devolver uma versão como
/// evidência canônica sem essa revalidação. RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlExoArchiveStatisticsStore(TenantConnectionFactory connectionFactory) : IExoArchiveStatisticsStore
{
    // SnapshotColumns = wave_id(0), archive_identity(1), phase(2), snapshot_version(3), tenant_id(4),
    // project_id(5), archive_status(6), exchange_guid(7), archive_guid(8), item_count(9),
    // total_item_size_bytes(10), total_deleted_item_size_bytes(11), last_logon_time_utc(12),
    // retention_hold_enabled(13), litigation_hold_enabled(14), auto_expanding_archive_enabled(15),
    // folder_count(16), folders_sha256(17), observation_hash(18), observed_at_utc(19), correlation_id(20),
    // created_at_utc(21), snapshot_hash(22).
    private const string SnapshotColumns =
        "wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id, archive_status, " +
        "exchange_guid, archive_guid, item_count, total_item_size_bytes, total_deleted_item_size_bytes, " +
        "last_logon_time_utc, retention_hold_enabled, litigation_hold_enabled, auto_expanding_archive_enabled, " +
        "folder_count, folders_sha256, observation_hash, observed_at_utc, correlation_id, created_at_utc, snapshot_hash";

    private const string GetLatestSql =
        $"""
        SELECT TOP (1) {SnapshotColumns} FROM dbo.purview_exo_archive_statistics_snapshots
        WHERE wave_id = @wave AND archive_identity = @archive AND phase = @phase AND project_id = @project
        ORDER BY snapshot_version DESC;
        """;

    private const string GetByVersionSql =
        $"""
        SELECT {SnapshotColumns} FROM dbo.purview_exo_archive_statistics_snapshots
        WHERE wave_id = @wave AND archive_identity = @archive AND phase = @phase AND snapshot_version = @version AND project_id = @project;
        """;

    // AB-I6-003 Blocker 3 (mesmo padrão): locka TODAS as versões existentes deste escopo para servir DUAS
    // decisões sob a MESMA seção crítica: (a) a próxima snapshot_version a alocar SE nenhuma versão
    // convergir, e (b) se alguma versão JÁ existente tem o MESMO observation_hash desta chamada — caso em
    // que a chamada converge para ela em vez de alocar N+1.
    private const string LockedVersionsSql =
        $"""
        SELECT {SnapshotColumns} FROM dbo.purview_exo_archive_statistics_snapshots WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND archive_identity = @archive AND phase = @phase AND project_id = @project
        ORDER BY snapshot_version DESC;
        """;

    private const string InsertSnapshotSql =
        """
        INSERT INTO dbo.purview_exo_archive_statistics_snapshots
            (wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id, archive_status,
             exchange_guid, archive_guid, item_count, total_item_size_bytes, total_deleted_item_size_bytes,
             last_logon_time_utc, retention_hold_enabled, litigation_hold_enabled, auto_expanding_archive_enabled,
             folder_count, folders_sha256, observation_hash, observed_at_utc, correlation_id, created_at_utc, snapshot_hash)
        VALUES
            (@wave, @archive, @phase, @version, @tenant, @project, @archiveStatus,
             @exchangeGuid, @archiveGuid, @itemCount, @totalItemSizeBytes, @totalDeletedItemSizeBytes,
             @lastLogonTimeUtc, @retentionHold, @litigationHold, @autoExpanding,
             @folderCount, @foldersSha256, @observationHash, @observedAt, @correlation, @createdAt, @snapshotHash);
        """;

    private const string FolderColumns =
        "folder_path, folder_type, items_in_folder, items_in_folder_and_subfolders, folder_size_bytes, " +
        "folder_and_subfolder_size_bytes, oldest_item_received_date_utc, newest_item_received_date_utc";

    private const string InsertFolderSql =
        $"""
        INSERT INTO dbo.purview_exo_archive_folder_statistics
            (wave_id, archive_identity, phase, snapshot_version, tenant_id, project_id, {FolderColumns})
        VALUES
            (@wave, @archive, @phase, @version, @tenant, @project, @folderPath, @folderType, @itemsInFolder,
             @itemsInFolderAndSub, @folderSizeBytes, @folderAndSubfolderSizeBytes, @oldest, @newest);
        """;

    private const string SelectFoldersSql =
        $"""
        SELECT {FolderColumns} FROM dbo.purview_exo_archive_folder_statistics
        WHERE wave_id = @wave AND archive_identity = @archive AND phase = @phase AND snapshot_version = @version AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<ExoArchiveStatisticsSnapshot> PersistAsync(
        TenantScope scope,
        WaveId wave,
        TargetArchiveId archive,
        ExoStatisticsPhase phase,
        MailboxArchiveStatus archiveStatus,
        Guid? exchangeGuid,
        Guid? archiveGuid,
        long? itemCount,
        long? totalItemSizeBytes,
        long? totalDeletedItemSizeBytes,
        DateTimeOffset? lastLogonTimeUtc,
        bool? retentionHoldEnabled,
        bool? litigationHoldEnabled,
        bool? autoExpandingArchiveEnabled,
        IReadOnlyList<ExoArchiveFolderStatistic> folders,
        DateTimeOffset observedAtUtc,
        CorrelationId correlation,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var foldersSha256 = ExoArchiveFolderStatisticsHash.Compute(folders);
        var candidateObservationHash = ExoArchiveStatisticsSnapshot.ComputeObservationHash(
            scope.Tenant, scope.Project, wave, archive, phase, archiveStatus, exchangeGuid, archiveGuid, itemCount,
            totalItemSizeBytes, totalDeletedItemSizeBytes, lastLogonTimeUtc, retentionHoldEnabled,
            litigationHoldEnabled, autoExpandingArchiveEnabled, foldersSha256, folders.Count, observedAtUtc);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand($"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}", connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(now));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "ExoArchiveStatistics", cancellationToken).ConfigureAwait(false);
            }

            int nextVersion = 1;
            ExoArchiveStatisticsSnapshot? converged = null;
            await using (var command = new SqlCommand(LockedVersionsSql, connection.Connection, transaction))
            {
                BindScope(command, wave, archive, phase, scope.Project);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var first = true;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (first)
                    {
                        nextVersion = reader.GetInt32(3) + 1; // ORDER BY snapshot_version DESC: primeira linha = maior versão.
                        first = false;
                    }

                    if (converged is null && string.Equals(reader.GetString(18).TrimEnd(), candidateObservationHash.Value, StringComparison.Ordinal))
                    {
                        // Outra chamada (concorrente ou anterior) já persistiu a MESMA observação lógica sob
                        // este lock — converge para ela em vez de alocar N+1.
                        converged = ReadSnapshot(reader);
                    }
                }
            }

            if (converged is not null)
            {
                _ = await ValidateAndLoadFoldersAsync(connection.Connection, transaction, scope, wave, archive, phase, converged, cancellationToken)
                    .ConfigureAwait(false);
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return converged;
            }

            var snapshot = ExoArchiveStatisticsSnapshot.Create(
                scope.Tenant, scope.Project, wave, archive, phase, nextVersion, archiveStatus, exchangeGuid, archiveGuid,
                itemCount, totalItemSizeBytes, totalDeletedItemSizeBytes, lastLogonTimeUtc, retentionHoldEnabled,
                litigationHoldEnabled, autoExpandingArchiveEnabled, folders.Count, foldersSha256, observedAtUtc,
                correlation, now);

            await using (var command = new SqlCommand(InsertSnapshotSql, connection.Connection, transaction))
            {
                BindScope(command, wave, archive, phase, scope.Project);
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = snapshot.SnapshotVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@archiveStatus", SqlDbType.TinyInt) { Value = (byte)snapshot.ArchiveStatus });
                command.Parameters.Add(new SqlParameter("@exchangeGuid", SqlDbType.UniqueIdentifier) { Value = (object?)snapshot.ExchangeGuid ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@archiveGuid", SqlDbType.UniqueIdentifier) { Value = (object?)snapshot.ArchiveGuid ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@itemCount", SqlDbType.BigInt) { Value = (object?)snapshot.ItemCount ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@totalItemSizeBytes", SqlDbType.BigInt) { Value = (object?)snapshot.TotalItemSizeBytes ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@totalDeletedItemSizeBytes", SqlDbType.BigInt) { Value = (object?)snapshot.TotalDeletedItemSizeBytes ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@lastLogonTimeUtc", SqlDbType.DateTime2)
                { Value = snapshot.LastLogonTimeUtc.HasValue ? SqlJobMapping.ToDbUtc(snapshot.LastLogonTimeUtc.Value) : DBNull.Value });
                command.Parameters.Add(new SqlParameter("@retentionHold", SqlDbType.Bit) { Value = (object?)snapshot.RetentionHoldEnabled ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@litigationHold", SqlDbType.Bit) { Value = (object?)snapshot.LitigationHoldEnabled ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@autoExpanding", SqlDbType.Bit) { Value = (object?)snapshot.AutoExpandingArchiveEnabled ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@folderCount", SqlDbType.Int) { Value = snapshot.FolderCount });
                command.Parameters.Add(new SqlParameter("@foldersSha256", SqlDbType.Char, 64) { Value = snapshot.FoldersSha256.Value });
                command.Parameters.Add(new SqlParameter("@observationHash", SqlDbType.Char, 64) { Value = snapshot.ObservationHash.Value });
                command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(snapshot.ObservedAtUtc) });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = snapshot.Correlation.Value });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(snapshot.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@snapshotHash", SqlDbType.Char, 64) { Value = snapshot.SnapshotHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var folder in folders)
            {
                await using var command = new SqlCommand(InsertFolderSql, connection.Connection, transaction);
                BindScope(command, wave, archive, phase, scope.Project);
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = snapshot.SnapshotVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@folderPath", SqlDbType.NVarChar, 400) { Value = folder.FolderPath });
                command.Parameters.Add(new SqlParameter("@folderType", SqlDbType.NVarChar, 100) { Value = folder.FolderType });
                command.Parameters.Add(new SqlParameter("@itemsInFolder", SqlDbType.BigInt) { Value = (object?)folder.ItemsInFolder ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@itemsInFolderAndSub", SqlDbType.BigInt) { Value = (object?)folder.ItemsInFolderAndSubfolders ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@folderSizeBytes", SqlDbType.BigInt) { Value = (object?)folder.FolderSizeBytes ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@folderAndSubfolderSizeBytes", SqlDbType.BigInt) { Value = (object?)folder.FolderAndSubfolderSizeBytes ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@oldest", SqlDbType.DateTime2)
                { Value = folder.OldestItemReceivedDateUtc.HasValue ? SqlJobMapping.ToDbUtc(folder.OldestItemReceivedDateUtc.Value) : DBNull.Value });
                command.Parameters.Add(new SqlParameter("@newest", SqlDbType.DateTime2)
                { Value = folder.NewestItemReceivedDateUtc.HasValue ? SqlJobMapping.ToDbUtc(folder.NewestItemReceivedDateUtc.Value) : DBNull.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ExoArchiveStatisticsSnapshot?> GetLatestAsync(
        TenantScope scope, WaveId wave, TargetArchiveId archive, ExoStatisticsPhase phase, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        ExoArchiveStatisticsSnapshot? snapshot;
        await using (var command = new SqlCommand(GetLatestSql, connection.Connection))
        {
            BindScope(command, wave, archive, phase, scope.Project);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            snapshot = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSnapshot(reader) : null;
        }

        if (snapshot is not null)
        {
            _ = await ValidateAndLoadFoldersAsync(connection.Connection, null, scope, wave, archive, phase, snapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExoArchiveFolderStatistic>> GetFoldersAsync(
        TenantScope scope, WaveId wave, TargetArchiveId archive, ExoStatisticsPhase phase, int snapshotVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        ExoArchiveStatisticsSnapshot snapshot;
        await using (var command = new SqlCommand(GetByVersionSql, connection.Connection))
        {
            BindScope(command, wave, archive, phase, scope.Project);
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = snapshotVersion });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            snapshot = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadSnapshot(reader)
                : throw new ExoArchiveStatisticsSourceNotFoundException("Versão de snapshot de estatísticas EXO inexistente/fora do escopo (fail-closed).");
        }

        return await ValidateAndLoadFoldersAsync(connection.Connection, null, scope, wave, archive, phase, snapshot, cancellationToken).ConfigureAwait(false);
    }

    // Rotina única compartilhada por GetLatestAsync, GetFoldersAsync e o ramo de convergência concorrente de
    // PersistAsync — carrega as estatísticas de pasta REALMENTE persistidas para a versão informada e
    // revalida, na MESMA chamada, que a contagem bate com folder_count E que o hash agregado recomputado
    // (ExoArchiveFolderStatisticsHash) bate com folders_sha256. Nenhum caminho que trata uma versão
    // persistida como evidência canônica pode pular esta revalidação.
    private static async Task<IReadOnlyList<ExoArchiveFolderStatistic>> ValidateAndLoadFoldersAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, WaveId wave, TargetArchiveId archive,
        ExoStatisticsPhase phase, ExoArchiveStatisticsSnapshot snapshot, CancellationToken cancellationToken)
    {
        var folders = new List<ExoArchiveFolderStatistic>(snapshot.FolderCount);
        await using (var command = transaction is null
            ? new SqlCommand(SelectFoldersSql, connection)
            : new SqlCommand(SelectFoldersSql, connection, transaction))
        {
            BindScope(command, wave, archive, phase, scope.Project);
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = snapshot.SnapshotVersion });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                folders.Add(ReadFolder(reader));
            }
        }

        if (folders.Count != snapshot.FolderCount)
        {
            throw new ExoArchiveStatisticsIntegrityViolationException(
                "A quantidade de estatísticas de pasta carregadas diverge de folder_count do snapshot — possível adulteração (fail-closed).");
        }

        var recomputedFoldersHash = ExoArchiveFolderStatisticsHash.Compute(folders);
        if (!string.Equals(recomputedFoldersHash.Value, snapshot.FoldersSha256.Value, StringComparison.Ordinal))
        {
            throw new ExoArchiveStatisticsIntegrityViolationException(
                "O hash agregado recomputado das estatísticas de pasta diverge de folders_sha256 — possível adulteração (fail-closed).");
        }

        return folders;
    }

    private static void BindScope(SqlCommand command, WaveId wave, TargetArchiveId archive, ExoStatisticsPhase phase, ProjectId project)
    {
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@archive", SqlDbType.NVarChar, 320) { Value = archive.Value });
        command.Parameters.Add(new SqlParameter("@phase", SqlDbType.TinyInt) { Value = (byte)phase });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
    }

    private static ExoArchiveStatisticsSnapshot ReadSnapshot(SqlDataReader reader) =>
        ExoArchiveStatisticsSnapshot.Rehydrate(
            new TenantId(reader.GetGuid(4)),
            new ProjectId(reader.GetGuid(5)),
            new WaveId(reader.GetGuid(0)),
            new TargetArchiveId(reader.GetString(1).TrimEnd()),
            (ExoStatisticsPhase)reader.GetByte(2),
            reader.GetInt32(3),
            (MailboxArchiveStatus)reader.GetByte(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
            reader.IsDBNull(13) ? null : reader.GetBoolean(13),
            reader.IsDBNull(14) ? null : reader.GetBoolean(14),
            reader.IsDBNull(15) ? null : reader.GetBoolean(15),
            reader.GetInt32(16),
            new Sha256Hash(reader.GetString(17).TrimEnd()),
            SqlJobMapping.ReadUtc(reader.GetDateTime(19)),
            new CorrelationId(reader.GetGuid(20)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(21)),
            new Sha256Hash(reader.GetString(18).TrimEnd()),
            new Sha256Hash(reader.GetString(22).TrimEnd()));

    // FolderColumns = folder_path(0), folder_type(1), items_in_folder(2), items_in_folder_and_subfolders(3),
    // folder_size_bytes(4), folder_and_subfolder_size_bytes(5), oldest_item_received_date_utc(6),
    // newest_item_received_date_utc(7).
    private static ExoArchiveFolderStatistic ReadFolder(SqlDataReader reader) =>
        new(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1).TrimEnd(),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(6)),
            reader.IsDBNull(7) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(7)));
}
