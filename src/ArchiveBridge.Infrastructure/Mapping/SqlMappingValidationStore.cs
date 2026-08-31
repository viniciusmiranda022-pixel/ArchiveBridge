using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Custódia SQL das tentativas de validação de upload de CSV de mapping. Append-only, tenant/projeto
/// scoped (RLS + filtro explícito), identidade da aplicação (nunca a de manutenção). A persistência é
/// idempotente pela chave (tenant+projeto+chave) — busca sob lock de range (UPDLOCK/HOLDLOCK), backstop
/// por índice único — e revalida a onda-fonte (versão/hashes/estado Approved|Frozen) na MESMA transação
/// curta (defesa TOCTOU). A tentativa e seus problemas são inseridos atomicamente.
/// </summary>
public sealed class SqlMappingValidationStore(TenantConnectionFactory connectionFactory) : IMappingValidationStore
{
    private const byte ApprovedStatus = (byte)WaveStatus.Approved; // 4
    private const byte FrozenStatus = (byte)WaveStatus.Frozen;     // 5

    // Busca a chave sob lock de RANGE: serializa concorrentes com a mesma chave dentro do tenant/projeto.
    private const string LookupSql =
        """
        SET NOCOUNT ON;
        SELECT validation_id, wave_id, wave_version, configuration_hash, selection_hash,
               mapping_schema_version, mapping_policy_version, content_code_page, content_sha256
        FROM dbo.mapping_validation_attempts WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND idempotency_key = @key;
        """;

    // Revalida a onda-fonte na MESMA transação (defesa TOCTOU) sob lock.
    private const string RevalidateWaveSql =
        """
        SET NOCOUNT ON;
        SELECT wave_version, configuration_hash, selection_hash, status
        FROM dbo.migration_waves WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND tenant_id = @tenant AND project_id = @project;
        """;

    private const string InsertAttemptSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.mapping_validation_attempts
            (validation_id, tenant_id, project_id, wave_id, wave_version, configuration_hash, selection_hash,
             mapping_schema_version, mapping_policy_version, content_code_page, content_sha256, size_bytes,
             row_count, outcome, issue_count, issues_truncated, display_file_name, user_id, uploaded_by,
             correlation_id, idempotency_key, created_at_utc)
        VALUES
            (@validationId, @tenant, @project, @wave, @waveVersion, @configHash, @selectionHash,
             @schemaVersion, @policyVersion, @codePage, @contentSha, @sizeBytes,
             @rowCount, @outcome, @issueCount, @truncated, @displayName, @userId, @uploadedBy,
             @correlation, @key, @created);
        """;

    private const string InsertIssueSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.mapping_validation_issues
            (validation_id, tenant_id, project_id, issue_ordinal, line_number, column_index, issue_code, message)
        VALUES (@validationId, @tenant, @project, @ordinal, @line, @column, @code, @message);
        """;

    private const string GetAttemptSql =
        """
        SET NOCOUNT ON;
        SELECT validation_id, wave_id, wave_version, configuration_hash, selection_hash,
               mapping_schema_version, mapping_policy_version, content_code_page, content_sha256, size_bytes,
               row_count, outcome, issue_count, issues_truncated, display_file_name, user_id, uploaded_by,
               correlation_id, idempotency_key, created_at_utc
        FROM dbo.mapping_validation_attempts
        WHERE validation_id = @validationId AND project_id = @project;
        """;

    private const string GetLatestValidationIdSql =
        """
        SET NOCOUNT ON;
        SELECT TOP (1) validation_id
        FROM dbo.mapping_validation_attempts
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY created_at_utc DESC;
        """;

    private const string GetIssuesSql =
        """
        SET NOCOUNT ON;
        SELECT issue_code, line_number, column_index, message
        FROM dbo.mapping_validation_issues
        WHERE validation_id = @validationId AND project_id = @project
        ORDER BY issue_ordinal ASC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<MappingValidationPersistResult> PersistAsync(
        MappingValidationAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        // Fail-closed na borda de infraestrutura (não só no caso de uso): chave/usuário vazios ⇒ nenhuma escrita.
        if (attempt.IdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(attempt));
        }

        if (attempt.UserId == Guid.Empty)
        {
            throw new ArgumentException("UserId é obrigatório.", nameof(attempt));
        }

        var scope = attempt.Scope;
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindByKeyAsync(connection.Connection, transaction, scope, attempt.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureSameRequest(existing.Value, attempt);
                // Devolve a EVIDÊNCIA CANÔNICA PERSISTIDA (a tentativa original), lida na mesma transação —
                // nunca a recalculada nesta requisição.
                var canonical = await ReadAttemptAsync(
                    connection.Connection, transaction, scope, existing.Value.ValidationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Tentativa idempotente encontrada mas não legível na mesma transação.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new MappingValidationPersistResult(canonical, Created: false, Replayed: true);
            }

            await RevalidateWaveAsync(connection.Connection, transaction, attempt, cancellationToken).ConfigureAwait(false);

            try
            {
                await InsertAttemptAsync(connection.Connection, transaction, attempt, cancellationToken).ConfigureAwait(false);
                await InsertIssuesAsync(connection.Connection, transaction, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException sql) when (sql.Number is 2601 or 2627)
            {
                // Backstop: a corrida escapou do lock e a outra transação já criou a chave — reconcilia.
                var raced = await FindByKeyAsync(connection.Connection, transaction, scope, attempt.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                EnsureSameRequest(raced.Value, attempt);
                var canonical = await ReadAttemptAsync(
                    connection.Connection, transaction, scope, raced.Value.ValidationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Tentativa idempotente encontrada mas não legível na mesma transação.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new MappingValidationPersistResult(canonical, Created: false, Replayed: true);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            // Recém-criada: a tentativa persistida é EXATAMENTE a de entrada (mesmos bytes/hash/desfecho/problemas).
            return new MappingValidationPersistResult(attempt, Created: true, Replayed: false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MappingValidationAttempt?> GetAsync(
        TenantScope scope, Guid validationId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        return await ReadAttemptAsync(connection.Connection, transaction: null, scope, validationId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MappingValidationAttempt?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        Guid? latestId = null;
        await using (var command = new SqlCommand(GetLatestValidationIdSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                latestId = reader.GetGuid(0);
            }
        }

        if (latestId is not { } validationId)
        {
            return null;
        }

        return await ReadAttemptAsync(connection.Connection, transaction: null, scope, validationId, cancellationToken).ConfigureAwait(false);
    }

    // Lê a tentativa CANÔNICA persistida (com seus problemas) por identificador, no escopo informado. Aceita
    // uma transação opcional para permitir a leitura na MESMA transação do replay (evidência consistente sob
    // lock). Escopo aplicado sempre por project_id (RLS + filtro explícito): id de outro tenant/projeto ⇒ null.
    private static async Task<MappingValidationAttempt?> ReadAttemptAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, Guid validationId, CancellationToken cancellationToken)
    {
        MappingValidationAttempt? attempt = null;
        await using (var command = new SqlCommand(GetAttemptSql, connection, transaction))
        {
            command.Parameters.Add(new SqlParameter("@validationId", SqlDbType.UniqueIdentifier) { Value = validationId });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                attempt = new MappingValidationAttempt(
                    reader.GetGuid(0),
                    scope,
                    new WaveId(reader.GetGuid(1)),
                    reader.GetInt32(2),
                    new Sha256Hash(reader.GetString(3).TrimEnd()),
                    new Sha256Hash(reader.GetString(4).TrimEnd()),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    new ContentCodePage(reader.GetInt32(7)),
                    new Sha256Hash(reader.GetString(8).TrimEnd()),
                    reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    (MappingValidationAttemptOutcome)reader.GetByte(11),
                    reader.GetInt32(12),
                    reader.GetBoolean(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.GetGuid(15),
                    reader.GetString(16),
                    new CorrelationId(reader.GetGuid(17)),
                    reader.GetGuid(18),
                    SqlJobMapping.ReadUtc(reader.GetDateTime(19)),
                    []);
            }
        }

        if (attempt is null)
        {
            return null;
        }

        var issues = new List<MappingValidationIssue>();
        await using (var command = new SqlCommand(GetIssuesSql, connection, transaction))
        {
            command.Parameters.Add(new SqlParameter("@validationId", SqlDbType.UniqueIdentifier) { Value = validationId });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new MappingValidationIssue(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.GetString(3)));
            }
        }

        return attempt with { Issues = issues };
    }

    private static async Task<ExistingAttempt?> FindByKeyAsync(
        SqlConnection connection, SqlTransaction transaction, TenantScope scope, Guid key, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(LookupSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = key });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExistingAttempt(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            reader.GetString(3).TrimEnd(),
            reader.GetString(4).TrimEnd(),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetString(8).TrimEnd());
    }

    private static async Task RevalidateWaveAsync(
        SqlConnection connection, SqlTransaction transaction, MappingValidationAttempt attempt, CancellationToken cancellationToken)
    {
        var scope = attempt.Scope;
        await using var command = new SqlCommand(RevalidateWaveSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = attempt.WaveId.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new MappingValidationStaleSourceException("A onda-fonte não está mais disponível no escopo.");
        }

        var currentVersion = reader.GetInt32(0);
        var currentConfig = reader.GetString(1).TrimEnd();
        var currentSelection = reader.GetString(2).TrimEnd();
        var currentStatus = reader.GetByte(3);

        var same = currentVersion == attempt.WaveVersion
            && string.Equals(currentConfig, attempt.ConfigurationHash.Value, StringComparison.Ordinal)
            && string.Equals(currentSelection, attempt.SelectionHash.Value, StringComparison.Ordinal)
            && currentStatus is ApprovedStatus or FrozenStatus;
        if (!same)
        {
            throw new MappingValidationStaleSourceException(
                "A onda-fonte mudou (versão/hashes/estado) desde a validação; custódia recusada (fail-closed).");
        }
    }

    private static async Task InsertAttemptAsync(
        SqlConnection connection, SqlTransaction transaction, MappingValidationAttempt attempt, CancellationToken cancellationToken)
    {
        var scope = attempt.Scope;
        await using var command = new SqlCommand(InsertAttemptSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@validationId", SqlDbType.UniqueIdentifier) { Value = attempt.ValidationId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = attempt.WaveId.Value });
        command.Parameters.Add(new SqlParameter("@waveVersion", SqlDbType.Int) { Value = attempt.WaveVersion });
        command.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = attempt.ConfigurationHash.Value });
        command.Parameters.Add(new SqlParameter("@selectionHash", SqlDbType.Char, 64) { Value = attempt.SelectionHash.Value });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.Int) { Value = attempt.MappingSchemaVersion });
        command.Parameters.Add(new SqlParameter("@policyVersion", SqlDbType.Int) { Value = attempt.MappingPolicyVersion });
        command.Parameters.Add(new SqlParameter("@codePage", SqlDbType.Int) { Value = attempt.ContentCodePage.Value });
        command.Parameters.Add(new SqlParameter("@contentSha", SqlDbType.Char, 64) { Value = attempt.ContentSha256.Value });
        command.Parameters.Add(new SqlParameter("@sizeBytes", SqlDbType.BigInt) { Value = attempt.SizeBytes });
        command.Parameters.Add(new SqlParameter("@rowCount", SqlDbType.Int) { Value = (object?)attempt.RowCount ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)attempt.Outcome });
        command.Parameters.Add(new SqlParameter("@issueCount", SqlDbType.Int) { Value = attempt.IssueCount });
        command.Parameters.Add(new SqlParameter("@truncated", SqlDbType.Bit) { Value = attempt.IssuesTruncated });
        command.Parameters.Add(new SqlParameter("@displayName", SqlDbType.NVarChar, 260) { Value = (object?)attempt.DisplayFileName ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = attempt.UserId });
        command.Parameters.Add(new SqlParameter("@uploadedBy", SqlDbType.NVarChar, 200) { Value = attempt.RequestedBy });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = attempt.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = attempt.IdempotencyKey });
        command.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Value = attempt.CreatedAtUtc.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertIssuesAsync(
        SqlConnection connection, SqlTransaction transaction, MappingValidationAttempt attempt, CancellationToken cancellationToken)
    {
        var scope = attempt.Scope;
        for (var ordinal = 0; ordinal < attempt.Issues.Count; ordinal++)
        {
            var issue = attempt.Issues[ordinal];
            await using var command = new SqlCommand(InsertIssueSql, connection, transaction);
            command.Parameters.Add(new SqlParameter("@validationId", SqlDbType.UniqueIdentifier) { Value = attempt.ValidationId });
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@ordinal", SqlDbType.Int) { Value = ordinal });
            command.Parameters.Add(new SqlParameter("@line", SqlDbType.Int) { Value = (object?)issue.LineNumber ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@column", SqlDbType.Int) { Value = (object?)issue.ColumnIndex ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 64) { Value = issue.Code });
            command.Parameters.Add(new SqlParameter("@message", SqlDbType.NVarChar, 300) { Value = Clamp(issue.Message, 300) });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // A equivalência de idempotência cobre onda/versão/hashes/esquema/política/code page/SHA (§34). NÃO
    // inclui display filename, RequestedBy/UserId nem CorrelationId. Divergência ⇒ conflito.
    private static void EnsureSameRequest(ExistingAttempt existing, MappingValidationAttempt attempt)
    {
        var same = existing.WaveId == attempt.WaveId.Value
            && existing.WaveVersion == attempt.WaveVersion
            && string.Equals(existing.ConfigurationHash, attempt.ConfigurationHash.Value, StringComparison.Ordinal)
            && string.Equals(existing.SelectionHash, attempt.SelectionHash.Value, StringComparison.Ordinal)
            && existing.MappingSchemaVersion == attempt.MappingSchemaVersion
            && existing.MappingPolicyVersion == attempt.MappingPolicyVersion
            && existing.ContentCodePage == attempt.ContentCodePage.Value
            && string.Equals(existing.ContentSha256, attempt.ContentSha256.Value, StringComparison.Ordinal);
        if (!same)
        {
            throw new MappingValidationConflictException(
                "A chave de idempotência já está vinculada a uma validação diferente (conteúdo/contexto divergente).");
        }
    }

    private static string Clamp(string value, int max) => value.Length <= max ? value : value[..max];

    private readonly record struct ExistingAttempt(
        Guid ValidationId,
        Guid WaveId,
        int WaveVersion,
        string ConfigurationHash,
        string SelectionHash,
        int MappingSchemaVersion,
        int MappingPolicyVersion,
        int ContentCodePage,
        string ContentSha256);
}
