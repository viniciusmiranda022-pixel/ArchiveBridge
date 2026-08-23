using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Store SQL append-only da história de tentativas de exportação (AB-4C-005 items 11/12/14). Cada
/// <see cref="AppendAsync"/> grava a tentativa, o manifesto (quando concluída) e os itens oversized
/// (quando houver) na MESMA transação, cercada (<see cref="SqlJobFence"/>) exatamente como
/// <c>SqlEvDiscoveryStore</c>. Nenhuma linha é atualizada ou removida.
/// </summary>
public sealed class SqlEvExportAttemptStore(TenantConnectionFactory connectionFactory, IClock clock) : IEvExportAttemptStore
{
    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private const string InsertAttemptSql =
        """
        INSERT INTO dbo.ev_export_attempts
            (attempt_id, request_id, tenant_id, project_id, connector_id, external_archive_id, attempt_number,
             outcome, result_code, blocking_reason, engine_version, connector_version, manifest_hash,
             process_exit_code, started_at_utc, completed_at_utc)
        VALUES
            (@attemptId, @requestId, @tenant, @project, @connector, @archiveId, @attemptNumber,
             @outcome, @resultCode, @blockingReason, @engineVersion, @connectorVersion, @manifestHash,
             @exitCode, @startedAt, @completedAt);
        """;

    private const string InsertManifestEntrySql =
        """
        INSERT INTO dbo.ev_export_manifest_entries (attempt_id, tenant_id, project_id, output_id, file_name_hint, size_bytes, content_sha256)
        VALUES (@attemptId, @tenant, @project, @outputId, @fileNameHint, @sizeBytes, @sha256);
        """;

    private const string InsertOversizedItemSql =
        """
        INSERT INTO dbo.ev_export_oversized_items
            (oversized_item_id, attempt_id, tenant_id, project_id, external_item_reference, size_bytes, classification, diagnostic, detected_at_utc)
        VALUES (@id, @attemptId, @tenant, @project, @reference, @sizeBytes, @classification, @diagnostic, @detectedAt);
        """;

    private const string AttemptColumns =
        "attempt_id, request_id, connector_id, external_archive_id, attempt_number, outcome, result_code, " +
        "blocking_reason, engine_version, connector_version, manifest_hash, process_exit_code, started_at_utc, completed_at_utc";

    private const string SelectLatestAttemptSql =
        $"SELECT TOP (1) {AttemptColumns} FROM dbo.ev_export_attempts " +
        "WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project ORDER BY attempt_number DESC;";

    private const string SelectAllAttemptsSql =
        $"SELECT {AttemptColumns} FROM dbo.ev_export_attempts " +
        "WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project ORDER BY attempt_number ASC;";

    private const string SelectManifestEntriesSql =
        "SELECT output_id, file_name_hint, size_bytes, content_sha256 FROM dbo.ev_export_manifest_entries " +
        "WHERE attempt_id = @attempt AND tenant_id = @tenant AND project_id = @project ORDER BY content_sha256 ASC;";

    private const string SelectOversizedItemsSql =
        "SELECT external_item_reference, size_bytes, classification, diagnostic, detected_at_utc FROM dbo.ev_export_oversized_items " +
        "WHERE attempt_id = @attempt AND tenant_id = @tenant AND project_id = @project ORDER BY external_item_reference ASC;";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task AppendAsync(TenantScope scope, EvExportAttemptRecord record, JobFence? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "EvExportAttempt", cancellationToken).ConfigureAwait(false);
            }

            await using (var insertAttempt = new SqlCommand(InsertAttemptSql, connection.Connection, transaction))
            {
                BindAttempt(insertAttempt, scope, record);
                await insertAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (record.Manifest is not null)
            {
                foreach (var entry in record.Manifest.Entries)
                {
                    await using var insertEntry = new SqlCommand(InsertManifestEntrySql, connection.Connection, transaction);
                    insertEntry.Parameters.Add(new SqlParameter("@attemptId", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
                    insertEntry.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                    insertEntry.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                    insertEntry.Parameters.Add(new SqlParameter("@outputId", SqlDbType.UniqueIdentifier) { Value = entry.Id.Value });
                    insertEntry.Parameters.Add(new SqlParameter("@fileNameHint", SqlDbType.NVarChar, 260) { Value = entry.FileNameHint });
                    insertEntry.Parameters.Add(new SqlParameter("@sizeBytes", SqlDbType.BigInt) { Value = entry.SizeBytes });
                    insertEntry.Parameters.Add(new SqlParameter("@sha256", SqlDbType.Char, 64) { Value = entry.ContentSha256.Value });
                    await insertEntry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var oversized in record.OversizedItems)
            {
                await using var insertOversized = new SqlCommand(InsertOversizedItemSql, connection.Connection, transaction);
                insertOversized.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
                insertOversized.Parameters.Add(new SqlParameter("@attemptId", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
                insertOversized.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                insertOversized.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                insertOversized.Parameters.Add(new SqlParameter("@reference", SqlDbType.NVarChar, 300) { Value = oversized.ExternalItemReference });
                insertOversized.Parameters.Add(new SqlParameter("@sizeBytes", SqlDbType.BigInt) { Value = oversized.SizeBytes });
                insertOversized.Parameters.Add(new SqlParameter("@classification", SqlDbType.TinyInt) { Value = (byte)oversized.Classification });
                insertOversized.Parameters.Add(new SqlParameter("@diagnostic", SqlDbType.NVarChar, 300)
                {
                    Value = (object?)oversized.Diagnostic ?? DBNull.Value,
                });
                insertOversized.Parameters.Add(
                    new SqlParameter("@detectedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(oversized.DetectedAtUtc) });
                await insertOversized.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence
                .RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
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
    public async Task<EvExportAttemptRecord?> GetLatestAsync(TenantScope scope, ExportRequestId request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        RawAttemptRow? raw;
        await using (var command = new SqlCommand(SelectLatestAttemptSql, connection.Connection))
        {
            BindScopeAndRequest(command, scope, request);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            raw = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAttempt(reader, request) : null;
        }

        return raw is null ? null : await HydrateChildrenAsync(connection.Connection, scope, raw.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvExportAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, ExportRequestId request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var raw = new List<RawAttemptRow>();
        await using (var command = new SqlCommand(SelectAllAttemptsSql, connection.Connection))
        {
            BindScopeAndRequest(command, scope, request);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw.Add(ReadAttempt(reader, request));
            }
        }

        var hydrated = new List<EvExportAttemptRecord>(raw.Count);
        foreach (var row in raw)
        {
            hydrated.Add(await HydrateChildrenAsync(connection.Connection, scope, row, cancellationToken).ConfigureAwait(false));
        }

        return hydrated;
    }

    private static void BindScopeAndRequest(SqlCommand command, TenantScope scope, ExportRequestId request)
    {
        command.Parameters.Add(new SqlParameter("@request", SqlDbType.UniqueIdentifier) { Value = request.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindAttempt(SqlCommand command, TenantScope scope, EvExportAttemptRecord record)
    {
        command.Parameters.Add(new SqlParameter("@attemptId", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
        command.Parameters.Add(new SqlParameter("@requestId", SqlDbType.UniqueIdentifier) { Value = record.Request.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = record.Connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = record.ExternalArchiveId });
        command.Parameters.Add(new SqlParameter("@attemptNumber", SqlDbType.Int) { Value = record.AttemptNumber });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@resultCode", SqlDbType.NVarChar, 64) { Value = record.ResultCode.Value });
        command.Parameters.Add(new SqlParameter("@blockingReason", SqlDbType.NVarChar, 300)
        {
            Value = (object?)record.BlockingReason ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@engineVersion", SqlDbType.NVarChar, 100)
        {
            Value = (object?)record.Manifest?.EngineVersion ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@connectorVersion", SqlDbType.NVarChar, 50)
        {
            Value = (object?)record.Manifest?.ConnectorVersion ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@manifestHash", SqlDbType.Char, 64)
        {
            Value = (object?)record.Manifest?.ManifestHash.Value ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@exitCode", SqlDbType.Int) { Value = (object?)record.ProcessExitCode ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.CompletedAtUtc) });
    }

    // Carrega a linha crua de ev_export_attempts junto dos TRÊS campos do manifesto (engine/connector
    // version, manifest hash) que só existem no BANCO como colunas da própria tentativa — nunca perdidos
    // entre a leitura e a hidratação das entradas filhas (ver HydrateChildrenAsync).
    private readonly record struct RawAttemptRow(EvExportAttemptRecord Record, string? EngineVersion, string? ConnectorVersion, string? ManifestHash);

    private static RawAttemptRow ReadAttempt(SqlDataReader reader, ExportRequestId request)
    {
        var record = new EvExportAttemptRecord(
            request,
            new ExportAttemptId(reader.GetGuid(0)),
            reader.GetInt32(4),
            new ConnectorId(reader.GetGuid(2)),
            reader.GetString(3),
            (EvExportAttemptOutcome)reader.GetByte(5),
            new EvExportResultCode(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            Manifest: null, // hidratado separadamente (HydrateChildrenAsync) — precisa das entradas filhas.
            OversizedItems: [],
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(13)));

        return new RawAttemptRow(
            record,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static async Task<EvExportAttemptRecord> HydrateChildrenAsync(
        SqlConnection connection, TenantScope scope, RawAttemptRow raw, CancellationToken cancellationToken)
    {
        var record = raw.Record;
        EvExportManifest? manifest = null;
        if (record.Outcome == EvExportAttemptOutcome.Completed)
        {
            var entries = new List<EvExportManifestEntry>();
            await using (var command = new SqlCommand(SelectManifestEntriesSql, connection))
            {
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    entries.Add(new EvExportManifestEntry(reader.GetString(1), reader.GetInt64(2), new Sha256Hash(reader.GetString(3))));
                }
            }

            manifest = EvExportManifest.Rehydrate(
                record.Request, record.Attempt, record.ExternalArchiveId, entries,
                RequireField(raw.EngineVersion, "engine_version"), RequireField(raw.ConnectorVersion, "connector_version"),
                new Sha256Hash(RequireField(raw.ManifestHash, "manifest_hash")));
        }

        var oversized = new List<EvOversizedNativeItem>();
        await using (var command = new SqlCommand(SelectOversizedItemsSql, connection))
        {
            command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                oversized.Add(new EvOversizedNativeItem(
                    reader.GetString(0), reader.GetInt64(1), (EvOversizedNativeItemClassification)reader.GetByte(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), SqlJobMapping.ReadUtc(reader.GetDateTime(4))));
            }
        }

        return record with { Manifest = manifest, OversizedItems = oversized };
    }

    // engine_version/connector_version/manifest_hash são NOT NULL no banco quando outcome = Completed
    // (reforçado por CK_ev_export_attempts_manifest_only_when_completed) — nunca chegam nulos aqui.
    private static string RequireField(string? value, string columnName) =>
        value ?? throw new InvalidOperationException($"{columnName} ausente para tentativa concluída (fail-closed).");
}
