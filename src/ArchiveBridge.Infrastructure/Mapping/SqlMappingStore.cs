using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Mapping;

/// <summary>
/// Persistência das versões de mapping e da evidência (linhas + sha256 + impressão digital + caminho
/// do artefato). Uma nova geração nunca sobrescreve. O protocolo é recuperável em DUAS transações
/// curtas, para NUNCA manter a transação SQL / os locks abertos durante o I/O de filesystem (crítico em
/// SMB/NAS): <see cref="ReserveAsync"/> (transação 1 — valida o cercamento, calcula N+1 sob lock e insere
/// a versão como <see cref="MappingVersionStatus.PendingArtifact"/>, SEM tocar o filesystem) → o chamador
/// publica o artefato imutável FORA do SQL → <see cref="FinalizeAsync"/> (transação 2 — valida o artefato
/// publicado ANTES de abrir a transação, revalida o cercamento, promove Pending→Usable e só então marca a
/// anterior como Superseded). O índice único filtrado impõe no máximo uma utilizável por onda. A role da
/// aplicação só pode atualizar a coluna <c>status</c> (hashes/fingerprint/artefato imutáveis). RLS por
/// SESSION_CONTEXT. Uma queda entre a reserva e a finalização é reconciliável por
/// <see cref="GetPendingByFingerprintAsync"/> — sem gerar versão indevida.
/// </summary>
public sealed class SqlMappingStore(TenantConnectionFactory connectionFactory, IClock clock) : IMappingStore
{
    private const byte StatusUsable = (byte)MappingVersionStatus.Usable;             // 0
    private const byte StatusSuperseded = (byte)MappingVersionStatus.Superseded;     // 1
    private const byte StatusPendingArtifact = (byte)MappingVersionStatus.PendingArtifact; // 2

    private const string MaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.mapping_csv_versions WHERE wave_id = @waveId AND project_id = @project;";

    // Lê o MAX sob lock (UPDLOCK, HOLDLOCK) DENTRO da transação de reserva: sequência atômica de
    // versão, impedindo que duas gerações concorrentes atribuam o mesmo N+1 (TOCTOU). Uma versão
    // PendingArtifact já conta no MAX — a próxima reserva pula para N+2.
    private const string LockedMaxVersionSql =
        "SELECT ISNULL(MAX(mapping_version), 0) FROM dbo.mapping_csv_versions WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE wave_id = @waveId AND project_id = @project;";

    // Marca a(s) versão(ões) utilizável(is) anterior(es) como Superseded. Só há no máximo uma (status=0).
    private const string SupersedeSql =
        "UPDATE dbo.mapping_csv_versions SET status = 1 WHERE wave_id = @waveId AND project_id = @project AND status = 0;";

    // Promove a reserva pendente desta versão a utilizável (só se ainda pendente). @@ROWCOUNT confirma.
    private const string PromoteSql =
        "SET NOCOUNT OFF; UPDATE dbo.mapping_csv_versions SET status = 0 " +
        "WHERE wave_id = @waveId AND project_id = @project AND mapping_version = @version AND status = 2;";

    private const string VersionColumns =
        "wave_id, mapping_version, project_id, configuration_hash, selection_hash, content_sha256, " +
        "row_count, validation_result, generated_by, created_at_utc, status, generation_fingerprint, artifact_path";

    private const string GetUsableSql =
        $"SELECT {VersionColumns} FROM dbo.mapping_csv_versions " +
        "WHERE wave_id = @waveId AND project_id = @project AND status = 0;";

    // Lê a linha da versão sob lock (UPDLOCK, HOLDLOCK) na transação de finalização (serializa uma
    // finalização concorrente da mesma reserva).
    private const string SelectByVersionSql =
        $"SELECT {VersionColumns} FROM dbo.mapping_csv_versions WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE wave_id = @waveId AND project_id = @project AND mapping_version = @version;";

    private const string GetPendingByFingerprintSql =
        """
        SELECT TOP (1) mapping_version, artifact_path, generation_fingerprint, content_sha256, artifact_size_bytes
        FROM dbo.mapping_csv_versions
        WHERE wave_id = @waveId AND project_id = @project AND generation_fingerprint = @fingerprint AND status = 2
        ORDER BY mapping_version ASC;
        """;

    private const string InsertVersionSql =
        """
        INSERT INTO dbo.mapping_csv_versions
            (wave_id, mapping_version, tenant_id, project_id, configuration_hash, selection_hash,
             content_sha256, row_count, validation_result, generated_by, created_at_utc, status,
             generation_fingerprint, artifact_path, artifact_size_bytes)
        VALUES
            (@waveId, @version, @tenant, @project, @cfgHash, @selHash, @contentHash, @rowCount,
             @validation, @generatedBy, @createdAt, @status, @fingerprint, @artifactPath, @artifactSize);
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

    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

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
    public async Task<MappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(GetUsableSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadVersion(reader);
    }

    /// <inheritdoc />
    public async Task<MappingReservation?> GetPendingByFingerprintAsync(
        TenantScope scope, WaveId waveId, MappingGenerationFingerprint fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(GetPendingByFingerprintSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = fingerprint.Value.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MappingReservation(
            waveId,
            new MappingVersion(reader.GetInt32(0)),
            reader.GetString(1),
            new MappingGenerationFingerprint(new Sha256Hash(reader.GetString(2).TrimEnd())),
            new Sha256Hash(reader.GetString(3).TrimEnd()),
            reader.GetInt64(4));
    }

    /// <inheritdoc />
    public async Task<MappingReservation> ReserveAsync(
        TenantScope scope,
        MappingGenerationResult result,
        long expectedSizeBytes,
        JobFence? fence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var source = result.Version;
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Cercamento do Job (inerte quando não durável): valida na MESMA transação, com lock retido
            // (UPDLOCK/HOLDLOCK) até o commit e lease válido, que o efeito é do dono corrente.
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "Mapping", cancellationToken).ConfigureAwait(false);
            }

            int nextVersion;
            await using (var command = new SqlCommand(LockedMaxVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = source.Wave.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                nextVersion = (scalar is int value ? value : 0) + 1;
            }

            var mappingVersion = new MappingVersion(nextVersion);
            var logicalPath = new MappingArtifactDescriptor(scope, source.Wave, mappingVersion).LogicalPath;

            // Insere a versão como PendingArtifact (NÃO utilizável). A anterior segue utilizável até a
            // finalização. NENHUM I/O de filesystem ocorre aqui — o artefato é publicado depois, fora do SQL.
            await using (var command = new SqlCommand(InsertVersionSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = source.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = source.Project.Value });
                command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = source.ConfigurationHash.Value });
                command.Parameters.Add(new SqlParameter("@selHash", SqlDbType.Char, 64) { Value = source.SelectionHash.Value });
                command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = source.ContentSha256.Value });
                command.Parameters.Add(new SqlParameter("@rowCount", SqlDbType.Int) { Value = source.RowCount });
                command.Parameters.Add(new SqlParameter("@validation", SqlDbType.TinyInt) { Value = (byte)source.Validation });
                command.Parameters.Add(new SqlParameter("@generatedBy", SqlDbType.NVarChar, 200) { Value = source.GeneratedBy });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(source.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = StatusPendingArtifact });
                command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = source.Fingerprint.Value.Value });
                command.Parameters.Add(new SqlParameter("@artifactPath", SqlDbType.NVarChar, 400) { Value = logicalPath });
                command.Parameters.Add(new SqlParameter("@artifactSize", SqlDbType.BigInt) { Value = expectedSizeBytes });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var rowNumber = 0;
            foreach (var row in result.Rows)
            {
                rowNumber++;
                await using var command = new SqlCommand(InsertRowSql, connection.Connection, transaction);
                command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = source.Wave.Value });
                command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = nextVersion });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = source.Project.Value });
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

            // Revalida o cercamento (instante fresco) imediatamente antes do commit.
            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new MappingReservation(source.Wave, mappingVersion, logicalPath, source.Fingerprint, source.ContentSha256, expectedSizeBytes);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MappingCsvVersion> FinalizeAsync(
        TenantScope scope,
        MappingReservation reservation,
        JobFence? fence,
        Func<CancellationToken, Task> validatePublishedArtifactAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(validatePublishedArtifactAsync);

        // Valida o artefato PUBLICADO ANTES de abrir a transação / adquirir qualquer lock: nenhum I/O de
        // filesystem (leitura do bundle, SMB/NAS) ocorre sob a transação SQL. Falha aqui aborta a
        // finalização sem tocar o banco (fail-closed).
        await validatePublishedArtifactAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "Mapping", cancellationToken).ConfigureAwait(false);
            }

            var current = await ReadByVersionAsync(connection.Connection, transaction, reservation.Wave, reservation.Version, scope, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new MappingGenerationException("Reserva de versão de mapping ausente na finalização (fail-closed).");

            // A reserva finalizada precisa corresponder EXATAMENTE ao que foi reservado (defesa contra
            // finalizar uma versão adulterada ou de outra geração).
            if (current.Fingerprint != reservation.Fingerprint
                || !string.Equals(current.ContentSha256.Value, reservation.ContentSha256.Value, StringComparison.Ordinal))
            {
                throw new MappingGenerationException("Versão reservada não corresponde à impressão digital/hash da finalização (fail-closed).");
            }

            if (current.Status is MappingVersionStatus.Usable or MappingVersionStatus.Superseded)
            {
                // Já finalizada por uma execução anterior (replay idempotente): nenhum novo efeito.
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            // PendingArtifact → primeiro substitui a utilizável anterior (0→1), depois promove esta (2→0):
            // nesta ordem o índice único filtrado (status=0) nunca vê duas utilizáveis.
            await using (var supersede = new SqlCommand(SupersedeSql, connection.Connection, transaction))
            {
                supersede.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = reservation.Wave.Value });
                supersede.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int promoted;
            await using (var promote = new SqlCommand(PromoteSql, connection.Connection, transaction))
            {
                promote.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = current.Wave.Value });
                promote.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                promote.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reservation.Version.Value });
                promoted = await promote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (promoted != 1)
            {
                throw new MappingGenerationException("Falha ao promover a reserva de mapping a utilizável (fail-closed).");
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return current with { Status = MappingVersionStatus.Usable };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<MappingCsvVersion?> ReadByVersionAsync(
        SqlConnection connection, SqlTransaction transaction, WaveId waveId, MappingVersion version, TenantScope scope, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SelectByVersionSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@waveId", SqlDbType.UniqueIdentifier) { Value = waveId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadVersion(reader);
    }

    private static MappingCsvVersion ReadVersion(SqlDataReader reader) =>
        new(
            new MappingVersion(reader.GetInt32(1)),
            new ProjectId(reader.GetGuid(2)),
            new WaveId(reader.GetGuid(0)),
            new Sha256Hash(reader.GetString(3).TrimEnd()),
            new Sha256Hash(reader.GetString(4).TrimEnd()),
            new Sha256Hash(reader.GetString(5).TrimEnd()),
            reader.GetInt32(6),
            (MappingValidationOutcome)reader.GetByte(7),
            reader.GetString(8),
            SqlJobMapping.ReadUtc(reader.GetDateTime(9)),
            (MappingVersionStatus)reader.GetByte(10),
            new MappingGenerationFingerprint(new Sha256Hash(reader.GetString(11).TrimEnd())),
            reader.GetString(12));
}
