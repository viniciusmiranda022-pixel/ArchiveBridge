using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview;

/// <summary>
/// Store SQL append-only de <see cref="MailboxPrecheckSnapshot"/>. O índice único
/// <c>(tenant_id, project_id, archive_identity, version)</c> é o backstop de concorrência — mesmo padrão
/// de <see cref="SqlCapabilityEvidenceStore"/>/<c>SqlConnectorInventoryStore</c> (AB-4C-002): converge
/// (Created=false) apenas quando o conteúdo lógico bate (<see cref="MailboxPrecheckSnapshot.IsSameContentAs"/>);
/// conteúdo diferente sinaliza <see cref="ConcurrencyException"/>. A leitura é fronteira NÃO CONFIÁVEL:
/// <see cref="GetLatestAsync"/> reidrata via <see cref="MailboxPrecheckSnapshot.Rehydrate"/>, que recomputa
/// o hash de adulteração e recusa fail-closed qualquer divergência.
/// </summary>
public sealed class SqlMailboxPrecheckStore(TenantConnectionFactory connectionFactory) : IMailboxPrecheckStore
{
    private const string SelectLatestSql =
        """
        SELECT TOP (1) snapshot_id, tenant_id, project_id, archive_identity, mailbox_display, version,
               exchange_guid, archive_guid, archive_status, recipient_type_details,
               auto_expanding_archive_enabled, litigation_hold_enabled, retention_hold_enabled,
               archive_item_count, archive_total_size_bytes, observed_available_bytes, observed_at_utc,
               correlation_id, recorded_at_utc, snapshot_hash
        FROM dbo.purview_mailbox_prechecks
        WHERE project_id = @project AND archive_identity = @archive
        ORDER BY version DESC;
        """;

    private const string SelectByVersionSql =
        """
        SELECT snapshot_id, tenant_id, project_id, archive_identity, mailbox_display, version,
               exchange_guid, archive_guid, archive_status, recipient_type_details,
               auto_expanding_archive_enabled, litigation_hold_enabled, retention_hold_enabled,
               archive_item_count, archive_total_size_bytes, observed_available_bytes, observed_at_utc,
               correlation_id, recorded_at_utc, snapshot_hash
        FROM dbo.purview_mailbox_prechecks
        WHERE project_id = @project AND archive_identity = @archive AND version = @version;
        """;

    private const string InsertSql =
        """
        INSERT INTO dbo.purview_mailbox_prechecks
            (snapshot_id, tenant_id, project_id, archive_identity, mailbox_display, version, exchange_guid,
             archive_guid, archive_status, recipient_type_details, auto_expanding_archive_enabled,
             litigation_hold_enabled, retention_hold_enabled, archive_item_count, archive_total_size_bytes,
             observed_available_bytes, observed_at_utc, correlation_id, recorded_at_utc, snapshot_hash)
        VALUES (@id, @tenant, @project, @archive, @mailbox, @version, @exchangeGuid, @archiveGuid, @status,
                @recipientType, @autoExpand, @litHold, @retHold, @itemCount, @totalSize, @availableBytes,
                @observedAt, @correlation, @recordedAt, @hash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<MailboxPrecheckSnapshot?> GetLatestAsync(
        TenantScope scope, TargetArchiveId mailbox, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectLatestSql, tenant.Connection);
        AddScopeParameters(command, scope, mailbox);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MailboxPrecheckAppendResult> AppendAsync(MailboxPrecheckSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var scope = new TenantScope(snapshot.Tenant, snapshot.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = snapshot.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = snapshot.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = snapshot.Project.Value });
        command.Parameters.Add(new SqlParameter("@archive", SqlDbType.NVarChar, 320) { Value = snapshot.Mailbox.Identity.Value });
        command.Parameters.Add(new SqlParameter("@mailbox", SqlDbType.NVarChar, 320) { Value = snapshot.Mailbox.Mailbox });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = snapshot.Version });
        command.Parameters.Add(new SqlParameter("@exchangeGuid", SqlDbType.UniqueIdentifier) { Value = (object?)snapshot.ExchangeGuid ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@archiveGuid", SqlDbType.UniqueIdentifier) { Value = (object?)snapshot.ArchiveGuid ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)snapshot.ArchiveStatus });
        command.Parameters.Add(new SqlParameter("@recipientType", SqlDbType.NVarChar, 100) { Value = (object?)snapshot.RecipientTypeDetails ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@autoExpand", SqlDbType.Bit) { Value = snapshot.AutoExpandingArchiveEnabled });
        command.Parameters.Add(new SqlParameter("@litHold", SqlDbType.Bit) { Value = snapshot.LitigationHoldEnabled });
        command.Parameters.Add(new SqlParameter("@retHold", SqlDbType.Bit) { Value = snapshot.RetentionHoldEnabled });
        command.Parameters.Add(new SqlParameter("@itemCount", SqlDbType.BigInt) { Value = (object?)snapshot.ArchiveItemCount ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@totalSize", SqlDbType.BigInt) { Value = (object?)snapshot.ArchiveTotalSizeBytes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@availableBytes", SqlDbType.BigInt) { Value = (object?)snapshot.ObservedAvailableBytes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(snapshot.ObservedAtUtc) });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = snapshot.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(snapshot.RecordedAtUtc) });
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = snapshot.SnapshotHash.Value });

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new MailboxPrecheckAppendResult(snapshot, Created: true);
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627)
        {
            // Colisão de versão: outra submissão concorrente do MESMO archive já gravou esta versão.
            // Converge (Created=false) SOMENTE se o conteúdo lógico bater; conteúdo diferente é
            // concorrência real — nunca mascarada como réplay.
            var existing = await GetByVersionAsync(scope, snapshot.Mailbox.Identity, snapshot.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            if (existing.IsSameContentAs(snapshot))
            {
                return new MailboxPrecheckAppendResult(existing, Created: false);
            }

            throw new ConcurrencyException(
                $"Archive {snapshot.Mailbox.Identity.Value}: a versão {snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                "já foi ocupada por outra submissão concorrente com conteúdo diferente. Releia o latest e tente novamente.");
        }
    }

    private async Task<MailboxPrecheckSnapshot?> GetByVersionAsync(
        TenantScope scope, TargetArchiveId mailbox, int version, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectByVersionSql, tenant.Connection);
        AddScopeParameters(command, scope, mailbox);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static void AddScopeParameters(SqlCommand command, TenantScope scope, TargetArchiveId mailbox)
    {
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@archive", SqlDbType.NVarChar, 320) { Value = mailbox.Value });
    }

    private static async Task<MailboxPrecheckSnapshot?> ReadOneAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var archiveIdentity = new TargetArchiveId(reader.GetString(3));
        var mailboxDisplay = reader.GetString(4);
        var mailbox = ArchiveRef.Rehydrate(mailboxDisplay, archiveIdentity, isIdentityResolved: true);

        return MailboxPrecheckSnapshot.Rehydrate(
            new PrecheckSnapshotId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            mailbox,
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            (MailboxArchiveStatus)reader.GetByte(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetBoolean(10),
            reader.GetBoolean(11),
            reader.GetBoolean(12),
            reader.IsDBNull(13) ? null : reader.GetInt64(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            reader.IsDBNull(15) ? null : reader.GetInt64(15),
            SqlJobMapping.ReadUtc(reader.GetDateTime(16)),
            new CorrelationId(reader.GetGuid(17)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(18)),
            new Sha256Hash(reader.GetString(19)));
    }
}
