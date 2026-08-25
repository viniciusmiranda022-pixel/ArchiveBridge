using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Persistência das avaliações de reconciliação expected-vs-observed e dos seus itens filhos de PST/archive
/// (AB-I6-007 itens 10-11). <see cref="PersistAsync"/> locka TODAS as versões existentes do escopo
/// (onda/plano) sob a MESMA transação e decide, sob esse lock, tanto a próxima
/// <c>assessment_version</c> QUANTO se alguma versão já existente converge pelo MESMO
/// <c>source_fingerprint</c> — chamadas concorrentes com o MESMO conjunto de evidências-fonte sempre
/// convergem para a MESMA versão, nunca alocam versões duplicadas (mesmo padrão de
/// <c>SqlExoArchiveStatisticsStore.PersistAsync</c>/<c>SqlPurviewServiceResultReportStore.PersistAsync</c>)
/// — e insere, na MESMA transação curta, o header e os itens filhos quando não há convergência (nunca em
/// transações separadas — nenhuma versão "parcial" é jamais visível). Toda leitura que trata uma versão
/// persistida como evidência canônica (latest, versão específica, ou o ramo de convergência concorrente de
/// <see cref="PersistAsync"/>) revalida os hashes REALMENTE persistidos: <c>ReconciliationAssessment.Rehydrate</c>
/// recomputa <c>assessment_hash</c> a partir dos campos do header, e os itens filhos realmente carregados
/// são recontados e rehashados contra <c>pst_item_count</c>/<c>pst_items_sha256</c> e
/// <c>archive_item_count</c>/<c>archive_items_sha256</c>. RLS por SESSION_CONTEXT.
/// <para>
/// Assim como <see cref="ServiceResult.SqlPurviewServiceResultReportStore"/>, o escopo natural no banco é
/// <c>(wave_id, attempt_sequence)</c> — <c>attempt_sequence</c> é resolvido a partir do
/// <see cref="PurviewImportJobName"/> planejado via <c>dbo.purview_import_job_plans</c>.
/// </para>
/// </summary>
public sealed class SqlReconciliationAssessmentStore(TenantConnectionFactory connectionFactory) : IReconciliationAssessmentStore
{
    private const string ResolveAttemptSequenceSql =
        "SELECT attempt_sequence FROM dbo.purview_import_job_plans WHERE wave_id = @wave AND project_id = @project AND planned_job_name = @name;";

    // AssessmentColumns = wave_id(0), attempt_sequence(1), assessment_version(2), tenant_id(3),
    // project_id(4), source_fingerprint(5), pst_item_count(6), pst_items_sha256(7), archive_item_count(8),
    // archive_items_sha256(9), correlation_id(10), created_at_utc(11), assessment_hash(12).
    private const string AssessmentColumns =
        "wave_id, attempt_sequence, assessment_version, tenant_id, project_id, source_fingerprint, " +
        "pst_item_count, pst_items_sha256, archive_item_count, archive_items_sha256, correlation_id, " +
        "created_at_utc, assessment_hash";

    private const string GetLatestSql =
        $"""
        SELECT TOP (1) {AssessmentColumns} FROM dbo.purview_reconciliation_assessments
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY assessment_version DESC;
        """;

    private const string GetByVersionSql =
        $"""
        SELECT {AssessmentColumns} FROM dbo.purview_reconciliation_assessments
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version AND project_id = @project;
        """;

    private const string LockedVersionsSql =
        $"""
        SELECT {AssessmentColumns} FROM dbo.purview_reconciliation_assessments WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND project_id = @project
        ORDER BY assessment_version DESC;
        """;

    private const string InsertAssessmentSql =
        """
        INSERT INTO dbo.purview_reconciliation_assessments
            (wave_id, attempt_sequence, assessment_version, tenant_id, project_id, source_fingerprint,
             pst_item_count, pst_items_sha256, archive_item_count, archive_items_sha256, correlation_id,
             created_at_utc, assessment_hash)
        VALUES
            (@wave, @attempt, @version, @tenant, @project, @sourceFingerprint,
             @pstItemCount, @pstItemsSha256, @archiveItemCount, @archiveItemsSha256, @correlation,
             @createdAt, @assessmentHash);
        """;

    private const string PstItemColumns =
        "remote_pst_name, disposition, observed_status, imported_item_count, imported_size_bytes, skipped_item_count, corrupted_item_count";

    private const string InsertPstItemSql =
        $"""
        INSERT INTO dbo.purview_reconciliation_pst_items
            (wave_id, attempt_sequence, assessment_version, tenant_id, project_id, {PstItemColumns})
        VALUES
            (@wave, @attempt, @version, @tenant, @project, @remoteName, @disposition, @observedStatus,
             @importedItems, @importedBytes, @skipped, @corrupted);
        """;

    private const string SelectPstItemsSql =
        $"""
        SELECT {PstItemColumns} FROM dbo.purview_reconciliation_pst_items
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version AND project_id = @project;
        """;

    private const string ArchiveItemColumns =
        "archive_identity, disposition, before_captured, after_captured, item_count_delta, total_item_size_bytes_delta";

    private const string InsertArchiveItemSql =
        $"""
        INSERT INTO dbo.purview_reconciliation_archive_items
            (wave_id, attempt_sequence, assessment_version, tenant_id, project_id, {ArchiveItemColumns})
        VALUES
            (@wave, @attempt, @version, @tenant, @project, @archive, @disposition, @beforeCaptured, @afterCaptured,
             @itemCountDelta, @sizeDelta);
        """;

    private const string SelectArchiveItemsSql =
        $"""
        SELECT {ArchiveItemColumns} FROM dbo.purview_reconciliation_archive_items
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND assessment_version = @version AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<ReconciliationAssessment> PersistAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        Sha256Hash mappingFingerprint,
        int? reportVersion,
        Sha256Hash? reportContentSha256,
        IReadOnlyList<ReconciliationArchiveEvidenceRef> archiveEvidence,
        IReadOnlyList<PstReconciliationItem> pstItems,
        IReadOnlyList<ArchiveReconciliationItem> archiveItems,
        CorrelationId correlation,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveEvidence);
        ArgumentNullException.ThrowIfNull(pstItems);
        ArgumentNullException.ThrowIfNull(archiveItems);

        var pstItemsSha256 = ReconciliationPstItemsHash.Compute(pstItems);
        var archiveItemsSha256 = ReconciliationArchiveItemsHash.Compute(archiveItems);
        var candidateFingerprint = ReconciliationAssessment.ComputeSourceFingerprint(
            scope.Tenant, scope.Project, wave, plannedJobName, mappingFingerprint, reportVersion, reportContentSha256, archiveEvidence);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand($"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}", connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(now));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "ReconciliationAssessment", cancellationToken).ConfigureAwait(false);
            }

            var attempt = await ResolveAttemptSequenceAsync(connection.Connection, transaction, scope, wave, plannedJobName, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new PurviewImportJobSourceNotFoundException(
                    "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

            int nextVersion = 1;
            ReconciliationAssessment? converged = null;
            await using (var command = new SqlCommand(LockedVersionsSql, connection.Connection, transaction))
            {
                BindScope(command, wave, attempt, scope.Project);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var first = true;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (first)
                    {
                        nextVersion = reader.GetInt32(2) + 1; // ORDER BY assessment_version DESC: primeira linha = maior versão.
                        first = false;
                    }

                    if (converged is null && string.Equals(reader.GetString(5).TrimEnd(), candidateFingerprint.Value, StringComparison.Ordinal))
                    {
                        // Outra chamada (concorrente ou anterior) já persistiu o MESMO conjunto de
                        // evidências-fonte sob este lock — converge para ela em vez de alocar N+1.
                        converged = ReadAssessment(reader, plannedJobName);
                    }
                }
            }

            if (converged is not null)
            {
                _ = await ValidateAndLoadPstItemsAsync(connection.Connection, transaction, scope, wave, attempt, converged, cancellationToken)
                    .ConfigureAwait(false);
                _ = await ValidateAndLoadArchiveItemsAsync(connection.Connection, transaction, scope, wave, attempt, converged, cancellationToken)
                    .ConfigureAwait(false);
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return converged;
            }

            var assessment = ReconciliationAssessment.Create(
                scope.Tenant, scope.Project, wave, plannedJobName, nextVersion, candidateFingerprint,
                pstItems.Count, pstItemsSha256, archiveItems.Count, archiveItemsSha256, correlation, now);

            await using (var command = new SqlCommand(InsertAssessmentSql, connection.Connection, transaction))
            {
                BindScope(command, wave, attempt, scope.Project);
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessment.AssessmentVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@sourceFingerprint", SqlDbType.Char, 64) { Value = assessment.SourceFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@pstItemCount", SqlDbType.Int) { Value = assessment.PstItemCount });
                command.Parameters.Add(new SqlParameter("@pstItemsSha256", SqlDbType.Char, 64) { Value = assessment.PstItemsSha256.Value });
                command.Parameters.Add(new SqlParameter("@archiveItemCount", SqlDbType.Int) { Value = assessment.ArchiveItemCount });
                command.Parameters.Add(new SqlParameter("@archiveItemsSha256", SqlDbType.Char, 64) { Value = assessment.ArchiveItemsSha256.Value });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = assessment.Correlation.Value });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(assessment.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@assessmentHash", SqlDbType.Char, 64) { Value = assessment.AssessmentHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var item in pstItems)
            {
                await using var command = new SqlCommand(InsertPstItemSql, connection.Connection, transaction);
                BindScope(command, wave, attempt, scope.Project);
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessment.AssessmentVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@remoteName", SqlDbType.NVarChar, 300) { Value = item.RemoteName.Value });
                command.Parameters.Add(new SqlParameter("@disposition", SqlDbType.TinyInt) { Value = (byte)item.Disposition });
                command.Parameters.Add(new SqlParameter("@observedStatus", SqlDbType.TinyInt)
                { Value = item.ObservedStatus.HasValue ? (byte)item.ObservedStatus.Value : DBNull.Value });
                command.Parameters.Add(new SqlParameter("@importedItems", SqlDbType.BigInt) { Value = (object?)item.ImportedItemCount ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@importedBytes", SqlDbType.BigInt) { Value = (object?)item.ImportedSizeBytes ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@skipped", SqlDbType.BigInt) { Value = (object?)item.SkippedItemCount ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@corrupted", SqlDbType.BigInt) { Value = (object?)item.CorruptedItemCount ?? DBNull.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var item in archiveItems)
            {
                await using var command = new SqlCommand(InsertArchiveItemSql, connection.Connection, transaction);
                BindScope(command, wave, attempt, scope.Project);
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessment.AssessmentVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@archive", SqlDbType.NVarChar, 320) { Value = item.Archive.Value });
                command.Parameters.Add(new SqlParameter("@disposition", SqlDbType.TinyInt) { Value = (byte)item.Disposition });
                command.Parameters.Add(new SqlParameter("@beforeCaptured", SqlDbType.Bit) { Value = item.BeforeCaptured });
                command.Parameters.Add(new SqlParameter("@afterCaptured", SqlDbType.Bit) { Value = item.AfterCaptured });
                command.Parameters.Add(new SqlParameter("@itemCountDelta", SqlDbType.BigInt) { Value = (object?)item.ItemCountDelta ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@sizeDelta", SqlDbType.BigInt) { Value = (object?)item.TotalItemSizeBytesDelta ?? DBNull.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return assessment;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ReconciliationAssessment?> GetLatestAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return null;
        }

        ReconciliationAssessment? assessment;
        await using (var command = new SqlCommand(GetLatestSql, connection.Connection))
        {
            BindScope(command, wave, attempt.Value, scope.Project);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            assessment = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAssessment(reader, plannedJobName) : null;
        }

        if (assessment is not null)
        {
            _ = await ValidateAndLoadPstItemsAsync(connection.Connection, null, scope, wave, attempt.Value, assessment, cancellationToken).ConfigureAwait(false);
            _ = await ValidateAndLoadArchiveItemsAsync(connection.Connection, null, scope, wave, attempt.Value, assessment, cancellationToken).ConfigureAwait(false);
        }

        return assessment;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PstReconciliationItem>> GetPstItemsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException("Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        var assessment = await GetByVersionAsync(connection.Connection, scope, wave, attempt, assessmentVersion, plannedJobName, cancellationToken)
            .ConfigureAwait(false);
        return await ValidateAndLoadPstItemsAsync(connection.Connection, null, scope, wave, attempt, assessment, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchiveReconciliationItem>> GetArchiveItemsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var attempt = await ResolveAttemptSequenceAsync(connection.Connection, null, scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException("Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        var assessment = await GetByVersionAsync(connection.Connection, scope, wave, attempt, assessmentVersion, plannedJobName, cancellationToken)
            .ConfigureAwait(false);
        return await ValidateAndLoadArchiveItemsAsync(connection.Connection, null, scope, wave, attempt, assessment, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReconciliationAssessment> GetByVersionAsync(
        SqlConnection connection, TenantScope scope, WaveId wave, int attempt, int assessmentVersion, PurviewImportJobName plannedJobName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(GetByVersionSql, connection);
        BindScope(command, wave, attempt, scope.Project);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessmentVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAssessment(reader, plannedJobName)
            : throw new PurviewImportJobSourceNotFoundException("Versão de avaliação de reconciliação inexistente/fora do escopo (fail-closed).");
    }

    // Rotina única compartilhada por GetLatestAsync, GetPstItemsAsync e o ramo de convergência concorrente
    // de PersistAsync — carrega os itens de PST REALMENTE persistidos para a versão informada e revalida,
    // na MESMA chamada, que a contagem bate com pst_item_count E que o hash agregado recomputado
    // (ReconciliationPstItemsHash) bate com pst_items_sha256.
    private static async Task<IReadOnlyList<PstReconciliationItem>> ValidateAndLoadPstItemsAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, WaveId wave, int attempt,
        ReconciliationAssessment assessment, CancellationToken cancellationToken)
    {
        var items = new List<PstReconciliationItem>(assessment.PstItemCount);
        await using (var command = transaction is null
            ? new SqlCommand(SelectPstItemsSql, connection)
            : new SqlCommand(SelectPstItemsSql, connection, transaction))
        {
            BindScope(command, wave, attempt, scope.Project);
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessment.AssessmentVersion });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadPstItem(reader));
            }
        }

        if (items.Count != assessment.PstItemCount)
        {
            throw new ReconciliationIntegrityViolationException(
                "A quantidade de itens de PST carregados diverge de pst_item_count da avaliação — possível adulteração (fail-closed).");
        }

        var recomputed = ReconciliationPstItemsHash.Compute(items);
        if (!string.Equals(recomputed.Value, assessment.PstItemsSha256.Value, StringComparison.Ordinal))
        {
            throw new ReconciliationIntegrityViolationException(
                "O hash agregado recomputado dos itens de PST diverge de pst_items_sha256 — possível adulteração (fail-closed).");
        }

        return items;
    }

    // Mesmo princípio de ValidateAndLoadPstItemsAsync, para os itens de archive.
    private static async Task<IReadOnlyList<ArchiveReconciliationItem>> ValidateAndLoadArchiveItemsAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, WaveId wave, int attempt,
        ReconciliationAssessment assessment, CancellationToken cancellationToken)
    {
        var items = new List<ArchiveReconciliationItem>(assessment.ArchiveItemCount);
        await using (var command = transaction is null
            ? new SqlCommand(SelectArchiveItemsSql, connection)
            : new SqlCommand(SelectArchiveItemsSql, connection, transaction))
        {
            BindScope(command, wave, attempt, scope.Project);
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = assessment.AssessmentVersion });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadArchiveItem(reader));
            }
        }

        if (items.Count != assessment.ArchiveItemCount)
        {
            throw new ReconciliationIntegrityViolationException(
                "A quantidade de itens de archive carregados diverge de archive_item_count da avaliação — possível adulteração (fail-closed).");
        }

        var recomputed = ReconciliationArchiveItemsHash.Compute(items);
        if (!string.Equals(recomputed.Value, assessment.ArchiveItemsSha256.Value, StringComparison.Ordinal))
        {
            throw new ReconciliationIntegrityViolationException(
                "O hash agregado recomputado dos itens de archive diverge de archive_items_sha256 — possível adulteração (fail-closed).");
        }

        return items;
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

    private static void BindScope(SqlCommand command, WaveId wave, int attempt, ProjectId project)
    {
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = attempt });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
    }

    private static ReconciliationAssessment ReadAssessment(SqlDataReader reader, PurviewImportJobName plannedJobName) =>
        ReconciliationAssessment.Rehydrate(
            new TenantId(reader.GetGuid(3)),
            new ProjectId(reader.GetGuid(4)),
            new WaveId(reader.GetGuid(0)),
            plannedJobName,
            reader.GetInt32(2),
            new Sha256Hash(reader.GetString(5).TrimEnd()),
            reader.GetInt32(6),
            new Sha256Hash(reader.GetString(7).TrimEnd()),
            reader.GetInt32(8),
            new Sha256Hash(reader.GetString(9).TrimEnd()),
            new CorrelationId(reader.GetGuid(10)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(11)),
            new Sha256Hash(reader.GetString(12).TrimEnd()));

    // PstItemColumns = remote_pst_name(0), disposition(1), observed_status(2), imported_item_count(3),
    // imported_size_bytes(4), skipped_item_count(5), corrupted_item_count(6).
    private static PstReconciliationItem ReadPstItem(SqlDataReader reader) =>
        new(
            PurviewRemotePstName.FromPersistedValue(reader.GetString(0).TrimEnd()),
            (ReconciliationDisposition)reader.GetByte(1),
            reader.IsDBNull(2) ? null : (PurviewServiceResultRowStatus)reader.GetByte(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));

    // ArchiveItemColumns = archive_identity(0), disposition(1), before_captured(2), after_captured(3),
    // item_count_delta(4), total_item_size_bytes_delta(5).
    private static ArchiveReconciliationItem ReadArchiveItem(SqlDataReader reader) =>
        new(
            new TargetArchiveId(reader.GetString(0).TrimEnd()),
            (ReconciliationDisposition)reader.GetByte(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
}
