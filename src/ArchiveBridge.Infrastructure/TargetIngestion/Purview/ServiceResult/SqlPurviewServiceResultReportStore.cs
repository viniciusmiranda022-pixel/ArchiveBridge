using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Persistência das versões do validation report / service result do Purview e das suas linhas
/// normalizadas (AB-I6-001 itens 6/9-10). <see cref="PersistAsync"/> locka TODAS as versões existentes do
/// plano sob a MESMA transação e decide, sob esse lock, tanto a próxima <c>report_version</c> QUANTO se
/// alguma versão já existente converge pelo MESMO <c>content_sha256</c> — chamadas concorrentes
/// byte-idênticas sempre convergem para a MESMA versão, nunca alocam versões duplicadas para o mesmo
/// conteúdo (AB-I6-003 Blocker 3) — e insere, na MESMA transação curta, os metadados de evidência, o
/// conteúdo bruto (custódia) e as linhas filhas quando não há convergência — nunca em transações separadas
/// (nunca existe uma versão "parcial" visível). Toda leitura que trata uma versão persistida como evidência
/// canônica (replay, latest, versão específica) revalida os bytes REALMENTE persistidos de
/// <c>raw_content</c> contra <c>raw_size_bytes</c>/<c>content_sha256</c> (AB-I6-003 Blocker 2), além do
/// hash agregado das linhas contra <c>rows_sha256</c> em <see cref="GetRowsAsync"/> (fail-closed sob
/// tampering). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlPurviewServiceResultReportStore(TenantConnectionFactory connectionFactory) : IPurviewServiceResultReportStore
{
    private const string ResolveAttemptSequenceSql =
        "SELECT attempt_sequence FROM dbo.purview_import_job_plans WHERE wave_id = @wave AND project_id = @project AND planned_job_name = @name;";

    // raw_content é sempre a ÚLTIMA coluna: toda leitura que trata a versão persistida como evidência
    // canônica (replay por content hash, latest para completeness, versão específica para GetRowsAsync)
    // revalida os bytes REALMENTE persistidos contra raw_size_bytes/content_sha256 (AB-I6-003 Blocker 2) —
    // evidence_hash NÃO cobre o conteúdo bruto em si (só o hash dele), então adulterar raw_content sem
    // tocar content_sha256/raw_size_bytes não seria detectado sem esta revalidação.
    private const string EvidenceColumns =
        "wave_id, attempt_sequence, report_version, tenant_id, project_id, content_sha256, rows_sha256, raw_size_bytes, " +
        "row_count, declared_total_rows, uploaded_by, created_at_utc, evidence_hash, raw_content";

    private const string GetByContentHashSql =
        $"""
        SELECT {EvidenceColumns} FROM dbo.purview_service_result_report_versions
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project AND content_sha256 = @contentHash;
        """;

    private const string GetLatestSql =
        $"""
        SELECT TOP (1) {EvidenceColumns} FROM dbo.purview_service_result_report_versions
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY report_version DESC;
        """;

    private const string GetByVersionSql =
        $"""
        SELECT {EvidenceColumns} FROM dbo.purview_service_result_report_versions
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project AND report_version = @version;
        """;

    // AB-I6-003 Blocker 3: locka TODAS as versões existentes deste plano (mesmo predicado/força de lock da
    // antiga SELECT MAX) para servir DUAS decisões sob a MESMA seção crítica: (a) a próxima
    // report_version a alocar SE nenhuma versão convergir, e (b) se alguma versão JÁ existente tem o
    // MESMO content_sha256 do conteúdo desta chamada — caso em que a chamada converge para ela em vez de
    // alocar N+1. Sem isso, duas importações concorrentes byte-idênticas que ambas leram
    // GetByContentHashAsync como "nenhuma versão ainda" fora da transação alocariam duas versões para o
    // MESMO conteúdo (ou uma delas perderia a corrida no índice único sem convergir).
    private const string LockedVersionsSql =
        $"""
        SELECT {EvidenceColumns} FROM dbo.purview_service_result_report_versions WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY report_version DESC;
        """;

    private const string InsertVersionSql =
        """
        INSERT INTO dbo.purview_service_result_report_versions
            (wave_id, attempt_sequence, report_version, tenant_id, project_id, content_sha256, rows_sha256, raw_content,
             raw_size_bytes, row_count, declared_total_rows, uploaded_by, created_at_utc, evidence_hash)
        VALUES
            (@wave, @attempt, @version, @tenant, @project, @contentHash, @rowsHash, @rawContent,
             @rawSize, @rowCount, @declaredTotalRows, @uploadedBy, @createdAt, @evidenceHash);
        """;

    private const string RowColumns = "remote_pst_name, status, imported_item_count, imported_size_bytes, skipped_item_count, corrupted_item_count";

    private const string InsertRowSql =
        $"""
        INSERT INTO dbo.purview_service_result_rows
            (wave_id, attempt_sequence, report_version, tenant_id, project_id, {RowColumns})
        VALUES
            (@wave, @attempt, @version, @tenant, @project, @remoteName, @status, @importedItems, @importedBytes, @skipped, @corrupted);
        """;

    private const string SelectRowsSql =
        $"""
        SELECT {RowColumns} FROM dbo.purview_service_result_rows
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND report_version = @version AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<PurviewServiceResultReportEvidence?> GetByContentHashAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, Sha256Hash contentSha256, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return null;
        }

        await using var command = new SqlCommand(GetByContentHashSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = contentSha256.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEvidence(reader, plannedJobName) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewServiceResultReportEvidence> PersistAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        ReadOnlyMemory<byte> rawBytes,
        IReadOnlyList<PurviewServiceResultRow> rows,
        int? declaredTotalRows,
        string uploadedBy,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var contentSha256 = DeterministicHash.ComputeBytes(rawBytes.Span);
        var rowsSha256 = PurviewServiceResultRowsHash.Compute(rows);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand($"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}", connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(now));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "PurviewServiceResultReport", cancellationToken).ConfigureAwait(false);
            }

            var attempt = await ResolveAttemptSequenceAsync(connection.Connection, transaction, scope, wave, plannedJobName, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new PurviewImportJobSourceNotFoundException(
                    "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

            int nextVersion = 1;
            PurviewServiceResultReportEvidence? converged = null;
            await using (var command = new SqlCommand(LockedVersionsSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var first = true;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (first)
                    {
                        nextVersion = reader.GetInt32(2) + 1; // ORDER BY report_version DESC: primeira linha = maior versão.
                        first = false;
                    }

                    if (converged is null && string.Equals(reader.GetString(5).TrimEnd(), contentSha256.Value, StringComparison.Ordinal))
                    {
                        // AB-I6-003 Blocker 3: outra chamada (concorrente ou anterior) já persistiu o MESMO
                        // conteúdo bruto sob este lock — converge para ela em vez de alocar N+1.
                        converged = ReadEvidence(reader, plannedJobName);
                    }
                }
            }

            if (converged is not null)
            {
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return converged;
            }

            var evidence = PurviewServiceResultReportEvidence.Create(
                scope.Tenant, scope.Project, wave, plannedJobName, nextVersion, contentSha256, rowsSha256, rawBytes.Length,
                rows.Count, declaredTotalRows, uploadedBy, now);

            await using (var command = new SqlCommand(InsertVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = evidence.ContentSha256.Value });
                command.Parameters.Add(new SqlParameter("@rowsHash", SqlDbType.Char, 64) { Value = evidence.RowsSha256.Value });
                command.Parameters.Add(new SqlParameter("@rawContent", SqlDbType.VarBinary, -1) { Value = rawBytes.ToArray() });
                command.Parameters.Add(new SqlParameter("@rawSize", SqlDbType.BigInt) { Value = evidence.RawSizeBytes });
                command.Parameters.Add(new SqlParameter("@rowCount", SqlDbType.Int) { Value = evidence.RowCount });
                command.Parameters.Add(new SqlParameter("@declaredTotalRows", SqlDbType.Int)
                { Value = (object?)evidence.DeclaredTotalRows ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@uploadedBy", SqlDbType.NVarChar, 200) { Value = evidence.UploadedBy });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(evidence.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@evidenceHash", SqlDbType.Char, 64) { Value = evidence.EvidenceHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var row in rows)
            {
                await using var command = new SqlCommand(InsertRowSql, connection.Connection, transaction);
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@remoteName", SqlDbType.NVarChar, 300) { Value = row.RemoteName.Value });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)row.Status });
                command.Parameters.Add(new SqlParameter("@importedItems", SqlDbType.BigInt) { Value = (object?)row.ImportedItemCount ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@importedBytes", SqlDbType.BigInt) { Value = (object?)row.ImportedSizeBytes ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@skipped", SqlDbType.BigInt) { Value = (object?)row.SkippedItemCount ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@corrupted", SqlDbType.BigInt) { Value = (object?)row.CorruptedItemCount ?? DBNull.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return evidence;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PurviewServiceResultReportEvidence?> GetLatestAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return null;
        }

        await using var command = new SqlCommand(GetLatestSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEvidence(reader, plannedJobName) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PurviewServiceResultRow>> GetRowsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int reportVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException("Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        PurviewServiceResultReportEvidence evidence;
        await using (var command = new SqlCommand(GetByVersionSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
            command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reportVersion });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            evidence = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadEvidence(reader, plannedJobName)
                : throw new PurviewImportJobSourceNotFoundException("Versão de service result report inexistente/fora do escopo (fail-closed).");
        }

        var rows = new List<PurviewServiceResultRow>(evidence.RowCount);
        await using (var command = new SqlCommand(SelectRowsSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
            command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reportVersion });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRow(reader));
            }
        }

        if (rows.Count != evidence.RowCount)
        {
            throw new PurviewServiceResultIntegrityViolationException(
                "A quantidade de linhas carregadas diverge de row_count da evidência — possível adulteração (fail-closed).");
        }

        var recomputedRowsHash = PurviewServiceResultRowsHash.Compute(rows);
        if (!string.Equals(recomputedRowsHash.Value, evidence.RowsSha256.Value, StringComparison.Ordinal))
        {
            throw new PurviewServiceResultIntegrityViolationException(
                "O hash agregado recomputado das linhas diverge de rows_sha256 — possível adulteração (fail-closed).");
        }

        return rows;
    }

    private static async Task<int?> ResolveAttemptSequenceAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName,
        CancellationToken cancellationToken)
    {
        await using var command = transaction is null
            ? new SqlCommand(ResolveAttemptSequenceSql, connection)
            : new SqlCommand(ResolveAttemptSequenceSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, 100) { Value = plannedJobName.Value });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int value ? value : null;
    }

    // EvidenceColumns = wave_id(0), attempt_sequence(1), report_version(2), tenant_id(3), project_id(4),
    // content_sha256(5), rows_sha256(6), raw_size_bytes(7), row_count(8), declared_total_rows(9),
    // uploaded_by(10), created_at_utc(11), evidence_hash(12), raw_content(13).
    private static PurviewServiceResultReportEvidence ReadEvidence(SqlDataReader reader, PurviewImportJobName plannedJobName)
    {
        var contentSha256 = new Sha256Hash(reader.GetString(5).TrimEnd());
        var rawSizeBytes = reader.GetInt64(7);
        ValidateRawContentIntegrity(reader.GetFieldValue<byte[]>(13), rawSizeBytes, contentSha256);

        return PurviewServiceResultReportEvidence.Rehydrate(
            new TenantId(reader.GetGuid(3)),
            new ProjectId(reader.GetGuid(4)),
            new WaveId(reader.GetGuid(0)),
            plannedJobName,
            reader.GetInt32(2),
            contentSha256,
            new Sha256Hash(reader.GetString(6).TrimEnd()),
            rawSizeBytes,
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.GetString(10),
            SqlJobMapping.ReadUtc(reader.GetDateTime(11)),
            new Sha256Hash(reader.GetString(12).TrimEnd()));
    }

    // Revalida os bytes REALMENTE persistidos (nunca expostos além deste método — item "não exponha
    // conteúdo bruto desnecessariamente") contra os metadados de custódia lidos na MESMA linha. Cobre
    // adulteração de raw_content com tamanho preservado (só o hash muda) e com tamanho divergente.
    private static void ValidateRawContentIntegrity(byte[] rawContent, long expectedSizeBytes, Sha256Hash expectedContentSha256)
    {
        if (rawContent.LongLength != expectedSizeBytes)
        {
            throw new PurviewServiceResultIntegrityViolationException(
                "O tamanho do raw_content persistido diverge de raw_size_bytes — possível adulteração (fail-closed).");
        }

        var recomputed = DeterministicHash.ComputeBytes(rawContent);
        if (!string.Equals(recomputed.Value, expectedContentSha256.Value, StringComparison.Ordinal))
        {
            throw new PurviewServiceResultIntegrityViolationException(
                "O SHA-256 recomputado do raw_content persistido diverge de content_sha256 — possível adulteração (fail-closed).");
        }
    }

    // RowColumns = remote_pst_name(0), status(1), imported_item_count(2), imported_size_bytes(3),
    // skipped_item_count(4), corrupted_item_count(5).
    private static PurviewServiceResultRow ReadRow(SqlDataReader reader) =>
        new(
            PurviewRemotePstName.FromPersistedValue(reader.GetString(0).TrimEnd()),
            (PurviewServiceResultRowStatus)reader.GetByte(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
}
