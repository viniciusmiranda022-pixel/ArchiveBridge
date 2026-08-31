using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Store SQL append-only da história de tentativas de upload (AB-I5-009 items 8/10/11/14; AB-I5-015 items
/// 2/6). Cada <see cref="AppendAsync"/> grava a tentativa cercada (<see cref="SqlJobFence"/>) e, quando
/// <see cref="PurviewUploadAttemptOutcome.Uploaded"/>, a manifestação por arquivo
/// (<c>dbo.purview_upload_attempt_manifest_items</c>) na MESMA transação. Nenhuma linha é atualizada ou
/// removida. Toda leitura revalida <c>manifest_hash</c> contra os itens REALMENTE carregados — a
/// persistência é fronteira NÃO CONFIÁVEL (mesmo princípio de <c>binding_hash</c>/<c>handle_hash</c>):
/// qualquer item inserido, removido, duplicado ou alterado é recusado fail-closed
/// (<see cref="PurviewUploadAttemptIntegrityViolationException"/>).
/// </summary>
public sealed class SqlPurviewUploadAttemptStore(TenantConnectionFactory connectionFactory) : IPurviewUploadAttemptStore
{
    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private const string InsertAttemptSql =
        """
        INSERT INTO dbo.purview_upload_attempts
            (attempt_id, request_id, tenant_id, project_id, attempt_number, identity_hash, outcome, blocking_reason,
             process_exit_code, binary_version, binary_sha256, expected_file_count, expected_total_bytes, remote_wave_segment,
             manifest_hash, started_at_utc, completed_at_utc)
        VALUES
            (@attemptId, @requestId, @tenant, @project, @attemptNumber, @identityHash, @outcome, @blockingReason,
             @exitCode, @binaryVersion, @binarySha256, @expectedFileCount, @expectedTotalBytes, @remotePrefix,
             @manifestHash, @startedAt, @completedAt);
        """;

    private const string InsertManifestItemSql =
        """
        INSERT INTO dbo.purview_upload_attempt_manifest_items
            (attempt_id, tenant_id, project_id, item_index, execution_id, remote_pst_name, output_hash, expected_size_bytes)
        VALUES
            (@attemptId, @tenant, @project, @itemIndex, @execution, @remoteName, @outputHash, @sizeBytes);
        """;

    private const string Columns =
        "attempt_id, request_id, attempt_number, identity_hash, outcome, blocking_reason, process_exit_code, " +
        "binary_version, binary_sha256, expected_file_count, expected_total_bytes, remote_wave_segment, manifest_hash, " +
        "started_at_utc, completed_at_utc";

    private const string SelectLatestSql =
        $"SELECT TOP (1) {Columns} FROM dbo.purview_upload_attempts " +
        "WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project ORDER BY attempt_number DESC;";

    private const string SelectLatestAcrossRequestsSql =
        $"SELECT TOP (1) request_id, {Columns} FROM dbo.purview_upload_attempts " +
        "WHERE tenant_id = @tenant AND project_id = @project ORDER BY completed_at_utc DESC;";

    private const string SelectAllSql =
        $"SELECT {Columns} FROM dbo.purview_upload_attempts " +
        "WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project ORDER BY attempt_number ASC;";

    private const string SelectManifestSql =
        "SELECT execution_id, remote_pst_name, output_hash, expected_size_bytes FROM dbo.purview_upload_attempt_manifest_items " +
        "WHERE attempt_id = @attempt AND tenant_id = @tenant AND project_id = @project ORDER BY item_index ASC;";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AppendAsync(TenantScope scope, PurviewUploadAttemptRecord record, JobFence? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(record.CompletedAtUtc));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "PurviewUploadAttempt", cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = new SqlCommand(InsertAttemptSql, connection.Connection, transaction))
            {
                BindAttempt(insert, scope, record);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // (AB-I5-015 item 2) A manifestação por arquivo é gravada NA MESMA transação do attempt — nunca
            // parcialmente persistida (ou a tentativa inteira grava a manifestação completa, ou nenhuma
            // linha de manifestação existe). Ordem de gravação = ordem canônica já garantida pelo Domain
            // (PurviewUploadEvidence ordena por Execution ao construir) — item_index é só o ordinal de
            // ARMAZENAMENTO, nunca a identidade do item (que é execution_id).
            if (record.Evidence is { } evidence)
            {
                var itemIndex = 0;
                foreach (var item in evidence.Manifest)
                {
                    await using var insertItem = new SqlCommand(InsertManifestItemSql, connection.Connection, transaction);
                    insertItem.Parameters.Add(new SqlParameter("@attemptId", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
                    insertItem.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                    insertItem.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                    insertItem.Parameters.Add(new SqlParameter("@itemIndex", SqlDbType.Int) { Value = itemIndex });
                    insertItem.Parameters.Add(new SqlParameter("@execution", SqlDbType.UniqueIdentifier) { Value = item.Execution.Value });
                    insertItem.Parameters.Add(new SqlParameter("@remoteName", SqlDbType.NVarChar, 300) { Value = item.RemoteName.Value });
                    insertItem.Parameters.Add(new SqlParameter("@outputHash", SqlDbType.Char, 64) { Value = item.OutputHash.Value });
                    insertItem.Parameters.Add(new SqlParameter("@sizeBytes", SqlDbType.BigInt) { Value = item.ExpectedSizeBytes });
                    await insertItem.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    itemIndex++;
                }
            }

            await SqlJobFence
                .RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(record.CompletedAtUtc), cancellationToken)
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
    public async Task<PurviewUploadAttemptRecord?> GetLatestAsync(
        TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        RawAttemptRow? raw = null;
        await using (var command = new SqlCommand(SelectLatestSql, connection.Connection))
        {
            BindScopeAndRequest(command, scope, request);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw = ReadRaw(reader);
            }
        }

        return raw is null ? null : await BuildRecordAsync(connection.Connection, scope, request, raw.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PurviewUploadAttemptRecord?> GetLatestAcrossRequestsAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        PurviewUploadRequestId request;
        RawAttemptRow raw;
        await using (var command = new SqlCommand(SelectLatestAcrossRequestsSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            request = new PurviewUploadRequestId(reader.GetGuid(0));
            raw = ReadRaw(reader, columnOffset: 1);
        }

        return await BuildRecordAsync(connection.Connection, scope, request, raw, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PurviewUploadAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var rawRows = new List<RawAttemptRow>();
        await using (var command = new SqlCommand(SelectAllSql, connection.Connection))
        {
            BindScopeAndRequest(command, scope, request);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rawRows.Add(ReadRaw(reader));
            }
        }

        var results = new List<PurviewUploadAttemptRecord>(rawRows.Count);
        foreach (var raw in rawRows)
        {
            results.Add(await BuildRecordAsync(connection.Connection, scope, request, raw, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static void BindScopeAndRequest(SqlCommand command, TenantScope scope, PurviewUploadRequestId request)
    {
        command.Parameters.Add(new SqlParameter("@request", SqlDbType.UniqueIdentifier) { Value = request.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindAttempt(SqlCommand command, TenantScope scope, PurviewUploadAttemptRecord record)
    {
        command.Parameters.Add(new SqlParameter("@attemptId", SqlDbType.UniqueIdentifier) { Value = record.Attempt.Value });
        command.Parameters.Add(new SqlParameter("@requestId", SqlDbType.UniqueIdentifier) { Value = record.Request.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@attemptNumber", SqlDbType.Int) { Value = record.AttemptNumber });
        command.Parameters.Add(new SqlParameter("@identityHash", SqlDbType.Char, 64) { Value = record.IdentityHash.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@blockingReason", SqlDbType.NVarChar, 200)
        {
            Value = (object?)record.BlockingReason ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@exitCode", SqlDbType.Int) { Value = (object?)record.ProcessExitCode ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@binaryVersion", SqlDbType.NVarChar, 50)
        {
            Value = (object?)record.Evidence?.Binary.Version ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@binarySha256", SqlDbType.Char, 64)
        {
            Value = (object?)record.Evidence?.Binary.Sha256.Value ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@expectedFileCount", SqlDbType.Int)
        {
            Value = (object?)record.Evidence?.ExpectedFileCount ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@expectedTotalBytes", SqlDbType.BigInt)
        {
            Value = (object?)record.Evidence?.ExpectedTotalBytes ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@remotePrefix", SqlDbType.NVarChar, 200)
        {
            Value = (object?)record.Evidence?.RemotePrefix.WaveSegment ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@manifestHash", SqlDbType.Char, 64)
        {
            Value = (object?)record.Evidence?.ManifestHash.Value ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.CompletedAtUtc) });
    }

    // Snapshot bruto de UMA linha de dbo.purview_upload_attempts, lido enquanto o SqlDataReader ainda está
    // aberto — nunca a linha final: a evidência (quando Uploaded) só é reconstruída DEPOIS, com uma consulta
    // separada à manifestação (não é possível abrir um segundo comando enquanto o primeiro reader segue lendo).
    private readonly record struct RawAttemptRow(
        Guid AttemptId,
        int AttemptNumber,
        string IdentityHash,
        PurviewUploadAttemptOutcome Outcome,
        string? BlockingReason,
        int? ProcessExitCode,
        string? BinaryVersion,
        string? BinarySha256,
        int? ExpectedFileCount,
        long? ExpectedTotalBytes,
        string? RemoteWaveSegment,
        string? ManifestHash,
        DateTime StartedAtUtc,
        DateTime CompletedAtUtc);

    // columnOffset desloca todos os índices em +1 quando a query prefixa a projeção com request_id
    // (SelectLatestAcrossRequestsSql) — Columns por si só nunca inclui request_id (só é necessário quando a
    // query não filtra por um pedido já conhecido).
    private static RawAttemptRow ReadRaw(SqlDataReader reader, int columnOffset = 0) => new(
        reader.GetGuid(0 + columnOffset),
        reader.GetInt32(2 + columnOffset),
        reader.GetString(3 + columnOffset).TrimEnd(),
        (PurviewUploadAttemptOutcome)reader.GetByte(4 + columnOffset),
        reader.IsDBNull(5 + columnOffset) ? null : reader.GetString(5 + columnOffset),
        reader.IsDBNull(6 + columnOffset) ? null : reader.GetInt32(6 + columnOffset),
        reader.IsDBNull(7 + columnOffset) ? null : reader.GetString(7 + columnOffset),
        reader.IsDBNull(8 + columnOffset) ? null : reader.GetString(8 + columnOffset).TrimEnd(),
        reader.IsDBNull(9 + columnOffset) ? null : reader.GetInt32(9 + columnOffset),
        reader.IsDBNull(10 + columnOffset) ? null : reader.GetInt64(10 + columnOffset),
        reader.IsDBNull(11 + columnOffset) ? null : reader.GetString(11 + columnOffset),
        reader.IsDBNull(12 + columnOffset) ? null : reader.GetString(12 + columnOffset).TrimEnd(),
        reader.GetDateTime(13 + columnOffset),
        reader.GetDateTime(14 + columnOffset));

    // Reconstrói o registro completo — para Uploaded, carrega e revalida a manifestação persistida (item
    // 2/6): o hash recomputado a partir dos itens REALMENTE carregados precisa corresponder EXATAMENTE ao
    // manifest_hash persistido, e os agregados (expected_file_count/expected_total_bytes) — mesmo já
    // redundantes com o Manifest — são cruzados como defesa em profundidade adicional contra adulteração
    // isolada dessas colunas sem tocar a manifestação. Qualquer divergência é fail-closed.
    private static async Task<PurviewUploadAttemptRecord> BuildRecordAsync(
        SqlConnection connection, TenantScope scope, PurviewUploadRequestId request, RawAttemptRow raw, CancellationToken cancellationToken)
    {
        if (raw.Outcome != PurviewUploadAttemptOutcome.Uploaded)
        {
            return new PurviewUploadAttemptRecord(
                request, new PurviewUploadAttemptId(raw.AttemptId), raw.AttemptNumber, new Sha256Hash(raw.IdentityHash), raw.Outcome,
                raw.BlockingReason, Evidence: null, raw.ProcessExitCode, SqlJobMapping.ReadUtc(raw.StartedAtUtc), SqlJobMapping.ReadUtc(raw.CompletedAtUtc));
        }

        var manifest = await LoadManifestAsync(connection, scope, raw.AttemptId, cancellationToken).ConfigureAwait(false);
        if (manifest.Count == 0)
        {
            throw new PurviewUploadAttemptIntegrityViolationException(
                $"A tentativa Uploaded {raw.AttemptId} não tem nenhum item de manifestação persistido (fail-closed) — evidência incompleta ou corrompida.");
        }

        PurviewUploadEvidence evidence;
        try
        {
            evidence = new PurviewUploadEvidence(
                new AzCopyBinaryIdentity(raw.BinaryVersion!, new Sha256Hash(raw.BinarySha256!)),
                PurviewRemoteUploadPrefix.FromPersistedSegment(raw.RemoteWaveSegment!),
                manifest);
        }
        catch (ArgumentException exception)
        {
            throw new PurviewUploadAttemptIntegrityViolationException(
                $"A manifestação persistida da tentativa {raw.AttemptId} é estruturalmente inválida (fail-closed) — possível adulteração.",
                exception);
        }

        if (!string.Equals(evidence.ManifestHash.Value, raw.ManifestHash, StringComparison.Ordinal))
        {
            throw new PurviewUploadAttemptIntegrityViolationException(
                $"O manifest_hash persistido para a tentativa {raw.AttemptId} não corresponde ao hash recomputado a partir " +
                "dos itens carregados — manifestação possivelmente adulterada ou corrompida.");
        }

        if (evidence.ExpectedFileCount != raw.ExpectedFileCount || evidence.ExpectedTotalBytes != raw.ExpectedTotalBytes)
        {
            throw new PurviewUploadAttemptIntegrityViolationException(
                $"Os agregados persistidos (expected_file_count/expected_total_bytes) da tentativa {raw.AttemptId} não " +
                "correspondem à manifestação carregada — possível adulteração isolada dessas colunas.");
        }

        return new PurviewUploadAttemptRecord(
            request, new PurviewUploadAttemptId(raw.AttemptId), raw.AttemptNumber, new Sha256Hash(raw.IdentityHash), raw.Outcome,
            raw.BlockingReason, evidence, raw.ProcessExitCode, SqlJobMapping.ReadUtc(raw.StartedAtUtc), SqlJobMapping.ReadUtc(raw.CompletedAtUtc));
    }

    private static async Task<List<PurviewUploadFileManifestItem>> LoadManifestAsync(
        SqlConnection connection, TenantScope scope, Guid attemptId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SelectManifestSql, connection);
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.UniqueIdentifier) { Value = attemptId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<PurviewUploadFileManifestItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new PurviewUploadFileManifestItem(
                new PartitionExecutionId(reader.GetGuid(0)),
                PurviewRemotePstName.FromPersistedValue(reader.GetString(1)),
                new Sha256Hash(reader.GetString(2).TrimEnd()),
                reader.GetInt64(3)));
        }

        return items;
    }
}
