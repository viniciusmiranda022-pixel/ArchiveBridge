using System.Data;
using ArchiveBridge.Contracts.Abstractions;
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
/// Store SQL do metadado opaco de <see cref="PurviewSasUploadHandle"/> (AB-I5-004 item 8). Diferente de
/// <c>SqlCapabilityEvidenceStore</c>/<c>SqlMailboxPrecheckStore</c> (append-only puro), este store MUTA a
/// linha do handle canônico nas transições de ciclo de vida — concorrência otimista por <c>row_version</c>
/// (mesmo padrão de <c>SqlWaveStore</c>). O backstop de canonicidade sob corrida (item 16) é o índice único
/// FILTRADO <c>UX_psuh_canonical_live</c> (migration 0027): duas tentativas concorrentes de criar o
/// PRIMEIRO handle "vivo" de uma wave nunca produzem dois — a perdedora recebe <see cref="ConcurrencyException"/>.
/// </summary>
public sealed class SqlPurviewSasUploadHandleStore(TenantConnectionFactory connectionFactory, IClock clock) : IPurviewSasUploadHandleStore
{
    private const int ConcurrencyError = 50040;

    private const string ColumnList =
        """
        handle_id, tenant_id, project_id, wave_id, generation, state, fingerprint, secret_store_reference,
               authorized_host, authorized_container, key_version, expires_at_utc, stored_at_utc,
               available_at_utc, consumed_at_utc, expired_at_utc, destroyed_at_utc, correlation_id,
               recorded_at_utc, handle_hash, row_version
        """;

    private const string SelectCanonicalSql =
        $"""
        SELECT TOP (1) {ColumnList}
        FROM dbo.purview_sas_upload_handles
        WHERE project_id = @project AND wave_id = @wave
        ORDER BY generation DESC;
        """;

    private const string SelectByIdSql =
        $"""
        SELECT {ColumnList}
        FROM dbo.purview_sas_upload_handles
        WHERE project_id = @project AND handle_id = @id;
        """;

    private const string InsertSql =
        """
        INSERT INTO dbo.purview_sas_upload_handles
            (handle_id, tenant_id, project_id, wave_id, generation, state, fingerprint, secret_store_reference,
             authorized_host, authorized_container, key_version, expires_at_utc, stored_at_utc, available_at_utc,
             consumed_at_utc, expired_at_utc, destroyed_at_utc, correlation_id, recorded_at_utc, handle_hash)
        OUTPUT INSERTED.row_version
        VALUES
            (@id, @tenant, @project, @wave, @generation, @state, @fingerprint, @secretRef, @host, @container,
             @keyVersion, @expiresAt, @storedAt, @availableAt, @consumedAt, @expiredAt, @destroyedAt,
             @correlation, @recordedAt, @hash);
        """;

    private static readonly string DestroyPreviousSql =
        $"""
        UPDATE dbo.purview_sas_upload_handles
        SET state = @state, destroyed_at_utc = @destroyedAt, recorded_at_utc = @recordedAt, handle_hash = @hash
        WHERE handle_id = @id AND project_id = @project AND row_version = @rowVersion;
        IF @@ROWCOUNT = 0 THROW {ConcurrencyError}, 'Handle de SAS alterado concorrentemente (row_version divergente).', 1;
        """;

    private static readonly string UpdateTransitionSql =
        $"""
        UPDATE dbo.purview_sas_upload_handles
        SET state = @state, available_at_utc = @availableAt, consumed_at_utc = @consumedAt,
            expired_at_utc = @expiredAt, destroyed_at_utc = @destroyedAt, recorded_at_utc = @recordedAt,
            handle_hash = @hash
        OUTPUT INSERTED.row_version
        WHERE handle_id = @id AND project_id = @project AND row_version = @rowVersion;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<PurviewSasUploadHandle?> GetCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectCanonicalSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PurviewSasUploadHandle?> GetByIdAsync(TenantScope scope, SasHandleId id, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectByIdSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id.Value });
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PurviewSasUploadHandle> ReplaceCanonicalAsync(
        TenantScope scope,
        WaveId wave,
        PurviewSasUploadHandle? expectedPrevious,
        PurviewSasUploadHandle candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedPrevious is not null)
            {
                var destroyedPrevious = expectedPrevious.Destroy(_clock.UtcNow);
                await using var destroyCommand = new SqlCommand(DestroyPreviousSql, tenant.Connection, transaction);
                destroyCommand.Parameters.Add(new SqlParameter("@state", SqlDbType.TinyInt) { Value = (byte)destroyedPrevious.State });
                destroyCommand.Parameters.Add(new SqlParameter("@destroyedAt", SqlDbType.DateTime2)
                { Value = SqlJobMapping.ToDbUtc(destroyedPrevious.DestroyedAtUtc!.Value) });
                destroyCommand.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2)
                { Value = SqlJobMapping.ToDbUtc(destroyedPrevious.RecordedAtUtc) });
                destroyCommand.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = destroyedPrevious.HandleHash.Value });
                destroyCommand.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = expectedPrevious.Id.Value });
                destroyCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                destroyCommand.Parameters.Add(new SqlParameter("@rowVersion", SqlDbType.Binary, 8) { Value = expectedPrevious.RowVersion.ToBytes() });

                try
                {
                    await destroyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException exception) when (exception.Number == ConcurrencyError)
                {
                    throw new ConcurrencyException(
                        $"Wave {wave.Value}: o handle canônico mudou concorrentemente antes da substituição.", exception);
                }
            }

            await using var insertCommand = new SqlCommand(InsertSql, tenant.Connection, transaction);
            BindCandidate(insertCommand, candidate);

            byte[] rowVersionBytes;
            try
            {
                var scalar = await insertCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                rowVersionBytes = (byte[])scalar!;
            }
            catch (SqlException exception) when (exception.Number is 2601 or 2627)
            {
                // Índice único filtrado UX_psuh_canonical_live: outra submissão concorrente já criou o
                // handle "vivo" da wave (corrida de PRIMEIRO intake, sem expectedPrevious). Nunca mascarado
                // como sucesso — o chamador releé o canônico atual e tenta de novo.
                throw new ConcurrencyException(
                    $"Wave {wave.Value}: já existe um handle de SAS vivo para esta wave (corrida de intake).", exception);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return WithRowVersion(candidate, RowVersion.FromBytes(rowVersionBytes));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PurviewSasUploadHandle> SaveTransitionAsync(PurviewSasUploadHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var scope = new TenantScope(handle.Tenant, handle.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(UpdateTransitionSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@state", SqlDbType.TinyInt) { Value = (byte)handle.State });
        command.Parameters.Add(new SqlParameter("@availableAt", SqlDbType.DateTime2)
        { Value = handle.AvailableAtUtc is { } a ? SqlJobMapping.ToDbUtc(a) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@consumedAt", SqlDbType.DateTime2)
        { Value = handle.ConsumedAtUtc is { } c ? SqlJobMapping.ToDbUtc(c) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@expiredAt", SqlDbType.DateTime2)
        { Value = handle.ExpiredAtUtc is { } e ? SqlJobMapping.ToDbUtc(e) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@destroyedAt", SqlDbType.DateTime2)
        { Value = handle.DestroyedAtUtc is { } d ? SqlJobMapping.ToDbUtc(d) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(handle.RecordedAtUtc) });
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = handle.HandleHash.Value });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = handle.Id.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = handle.Project.Value });
        command.Parameters.Add(new SqlParameter("@rowVersion", SqlDbType.Binary, 8) { Value = handle.RowVersion.ToBytes() });

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is not byte[] rowVersionBytes)
        {
            throw new ConcurrencyException(
                $"Handle {handle.Id.Value}: alterado concorrentemente (row_version divergente); releia o estado atual.");
        }

        return WithRowVersion(handle, RowVersion.FromBytes(rowVersionBytes));
    }

    private static void BindCandidate(SqlCommand command, PurviewSasUploadHandle candidate)
    {
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = candidate.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = candidate.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = candidate.Project.Value });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = candidate.Wave.Value });
        command.Parameters.Add(new SqlParameter("@generation", SqlDbType.Int) { Value = candidate.Generation });
        command.Parameters.Add(new SqlParameter("@state", SqlDbType.TinyInt) { Value = (byte)candidate.State });
        command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = candidate.Fingerprint.Value });
        command.Parameters.Add(new SqlParameter("@secretRef", SqlDbType.NVarChar, 200) { Value = candidate.SecretStoreReference.Value });
        command.Parameters.Add(new SqlParameter("@host", SqlDbType.NVarChar, 300) { Value = candidate.AuthorizedHost });
        command.Parameters.Add(new SqlParameter("@container", SqlDbType.NVarChar, 100) { Value = candidate.AuthorizedContainer });
        command.Parameters.Add(new SqlParameter("@keyVersion", SqlDbType.Int) { Value = (object?)candidate.KeyVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@expiresAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(candidate.ExpiresAtUtc) });
        command.Parameters.Add(new SqlParameter("@storedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(candidate.StoredAtUtc) });
        command.Parameters.Add(new SqlParameter("@availableAt", SqlDbType.DateTime2)
        { Value = candidate.AvailableAtUtc is { } a ? SqlJobMapping.ToDbUtc(a) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@consumedAt", SqlDbType.DateTime2)
        { Value = candidate.ConsumedAtUtc is { } c ? SqlJobMapping.ToDbUtc(c) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@expiredAt", SqlDbType.DateTime2)
        { Value = candidate.ExpiredAtUtc is { } e ? SqlJobMapping.ToDbUtc(e) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@destroyedAt", SqlDbType.DateTime2)
        { Value = candidate.DestroyedAtUtc is { } d ? SqlJobMapping.ToDbUtc(d) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = candidate.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(candidate.RecordedAtUtc) });
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = candidate.HandleHash.Value });
    }

    private static PurviewSasUploadHandle WithRowVersion(PurviewSasUploadHandle handle, RowVersion rowVersion) =>
        PurviewSasUploadHandle.Rehydrate(
            handle.Id, handle.Tenant, handle.Project, handle.Wave, handle.Generation, handle.State, handle.Fingerprint,
            handle.SecretStoreReference, handle.AuthorizedHost, handle.AuthorizedContainer, handle.KeyVersion,
            handle.ExpiresAtUtc, handle.StoredAtUtc, handle.AvailableAtUtc, handle.ConsumedAtUtc, handle.ExpiredAtUtc,
            handle.DestroyedAtUtc, handle.Correlation, handle.RecordedAtUtc, rowVersion, handle.HandleHash);

    private static async Task<PurviewSasUploadHandle?> ReadOneAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var rowVersionBytes = new byte[8];
        _ = reader.GetBytes(20, 0, rowVersionBytes, 0, 8);

        return PurviewSasUploadHandle.Rehydrate(
            new SasHandleId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new WaveId(reader.GetGuid(3)),
            reader.GetInt32(4),
            (SasHandleState)reader.GetByte(5),
            new Sha256Hash(reader.GetString(6)),
            new SecretStoreHandleReference(reader.GetString(7)),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            SqlJobMapping.ReadUtc(reader.GetDateTime(11)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
            reader.IsDBNull(13) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(13)),
            reader.IsDBNull(14) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(14)),
            reader.IsDBNull(15) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(15)),
            reader.IsDBNull(16) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(16)),
            new CorrelationId(reader.GetGuid(17)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(18)),
            RowVersion.FromBytes(rowVersionBytes),
            new Sha256Hash(reader.GetString(19)));
    }
}
