using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Store SQL append-only da história de tentativas de upload (AB-I5-009 items 8/10/11/14) — mesmo padrão de
/// <c>SqlEvExportAttemptStore</c>. Cada <see cref="AppendAsync"/> grava a tentativa cercada
/// (<see cref="SqlJobFence"/>) na MESMA transação. Nenhuma linha é atualizada ou removida.
/// <see cref="PurviewUploadEvidence"/> só é persistida (colunas não-nulas) quando o desfecho é
/// <see cref="PurviewUploadAttemptOutcome.Uploaded"/> — nunca stdout/stderr bruto do AzCopy (item 10).
/// </summary>
public sealed class SqlPurviewUploadAttemptStore(TenantConnectionFactory connectionFactory) : IPurviewUploadAttemptStore
{
    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private const string InsertAttemptSql =
        """
        INSERT INTO dbo.purview_upload_attempts
            (attempt_id, request_id, tenant_id, project_id, attempt_number, identity_hash, outcome, blocking_reason,
             process_exit_code, binary_version, binary_sha256, expected_file_count, expected_total_bytes, remote_wave_segment,
             started_at_utc, completed_at_utc)
        VALUES
            (@attemptId, @requestId, @tenant, @project, @attemptNumber, @identityHash, @outcome, @blockingReason,
             @exitCode, @binaryVersion, @binarySha256, @expectedFileCount, @expectedTotalBytes, @remotePrefix,
             @startedAt, @completedAt);
        """;

    private const string Columns =
        "attempt_id, request_id, attempt_number, identity_hash, outcome, blocking_reason, process_exit_code, " +
        "binary_version, binary_sha256, expected_file_count, expected_total_bytes, remote_wave_segment, started_at_utc, completed_at_utc";

    private const string SelectLatestSql =
        $"SELECT TOP (1) {Columns} FROM dbo.purview_upload_attempts " +
        "WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project ORDER BY attempt_number DESC;";

    private const string SelectAllSql =
        $"SELECT {Columns} FROM dbo.purview_upload_attempts " +
        "WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project ORDER BY attempt_number ASC;";

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
        await using var command = new SqlCommand(SelectLatestSql, connection.Connection);
        BindScopeAndRequest(command, scope, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAttempt(reader, request) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PurviewUploadAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectAllSql, connection.Connection);
        BindScopeAndRequest(command, scope, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<PurviewUploadAttemptRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadAttempt(reader, request));
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
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.CompletedAtUtc) });
    }

    private static PurviewUploadAttemptRecord ReadAttempt(SqlDataReader reader, PurviewUploadRequestId request)
    {
        var outcome = (PurviewUploadAttemptOutcome)reader.GetByte(4);
        PurviewUploadEvidence? evidence = null;
        if (outcome == PurviewUploadAttemptOutcome.Uploaded)
        {
            var binary = new AzCopyBinaryIdentity(reader.GetString(7), new Sha256Hash(reader.GetString(8).TrimEnd()));
            evidence = new PurviewUploadEvidence(
                binary, reader.GetInt32(9), reader.GetInt64(10), PurviewRemoteUploadPrefix.FromPersistedSegment(reader.GetString(11)));
        }

        return new PurviewUploadAttemptRecord(
            request,
            new PurviewUploadAttemptId(reader.GetGuid(0)),
            reader.GetInt32(2),
            new Sha256Hash(reader.GetString(3).TrimEnd()),
            outcome,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            evidence,
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(13)));
    }
}
