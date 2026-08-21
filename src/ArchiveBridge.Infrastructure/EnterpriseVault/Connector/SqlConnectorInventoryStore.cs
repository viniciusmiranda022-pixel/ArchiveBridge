using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Connector;

/// <summary>
/// Store SQL append-only de snapshots de inventário (pai + archives filhos). O índice único
/// <c>(connector_id, version)</c> é o backstop de concorrência: duas submissões concorrentes calculando a
/// MESMA próxima versão para o mesmo connector nunca duplicam nem corrompem a linha vencedora. O que a
/// segunda submissão recebe depende do CONTEÚDO, nunca só da versão colidida (AB-4C-002): se o snapshot já
/// persistido nessa versão tem o MESMO <see cref="Sha256Hash"/> do candidate, é um réplay idêntico e
/// converge (<c>Created=false</c>); se o hash diverge, a versão foi legitimamente ocupada por uma mudança
/// real de outro writer — <see cref="AppendAsync"/> lança <see cref="ConcurrencyException"/> em vez de
/// mascarar a mudança perdida como réplay; a Application (<c>SubmitInventorySnapshotUseCase</c>) releé o
/// latest e tenta de novo com a próxima versão disponível.
/// <para>
/// A leitura também é fronteira NÃO CONFIÁVEL (AB-4C-003): <see cref="GetLatestAsync"/> e o probe interno de
/// conflito reidratam via <see cref="InventorySnapshot.Rehydrate"/>, que recomputa o hash a partir dos
/// archives filhos REALMENTE carregados e valida <c>archive_count</c> contra essa mesma quantidade —
/// nenhuma linha corrompida ou adulterada é devolvida como snapshot canônico.
/// </para>
/// </summary>
public sealed class SqlConnectorInventoryStore(TenantConnectionFactory connectionFactory) : IConnectorInventoryStore
{
    private const string SelectLatestSnapshotSql =
        """
        SELECT TOP (1) snapshot_id, connector_id, tenant_id, project_id, version, snapshot_hash,
               archive_count, correlation_id, collected_at_utc
        FROM dbo.ev_connector_inventory_snapshots
        WHERE connector_id = @connector AND project_id = @project
        ORDER BY version DESC;
        """;

    private const string SelectSnapshotByConnectorVersionSql =
        """
        SELECT snapshot_id, connector_id, tenant_id, project_id, version, snapshot_hash, archive_count,
               correlation_id, collected_at_utc
        FROM dbo.ev_connector_inventory_snapshots
        WHERE connector_id = @connector AND project_id = @project AND version = @version;
        """;

    private const string SelectArchivesSql =
        """
        SELECT external_archive_id, archive_type, vault_store_name, status, capability_diagnostics
        FROM dbo.ev_connector_inventory_archives
        WHERE snapshot_id = @snapshot
        ORDER BY external_archive_id;
        """;

    private const string InsertSnapshotSql =
        """
        INSERT INTO dbo.ev_connector_inventory_snapshots
            (snapshot_id, connector_id, tenant_id, project_id, version, snapshot_hash, archive_count,
             correlation_id, collected_at_utc)
        VALUES (@id, @connector, @tenant, @project, @version, @hash, @count, @correlation, @collectedAt);
        """;

    private const string InsertArchiveSql =
        """
        INSERT INTO dbo.ev_connector_inventory_archives
            (snapshot_id, tenant_id, project_id, external_archive_id, archive_type, vault_store_name, status,
             capability_diagnostics)
        VALUES (@snapshot, @tenant, @project, @externalId, @type, @vaultStore, @status, @diagnostics);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<InventorySnapshot?> GetLatestAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectLatestSnapshotSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        var header = await ReadSnapshotHeaderAsync(command, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var archives = await ReadArchivesAsync(tenant.Connection, null, header.Value.Id, cancellationToken).ConfigureAwait(false);
        return InventorySnapshot.Rehydrate(
            header.Value.Id, header.Value.Connector, header.Value.Tenant, header.Value.Project, header.Value.Version,
            header.Value.SnapshotHash, header.Value.ArchiveCount, archives, header.Value.Correlation, header.Value.CollectedAtUtc);
    }

    /// <inheritdoc />
    public async Task<InventorySnapshotAppendResult> AppendAsync(InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var scope = new TenantScope(snapshot.Tenant, snapshot.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var insertSnapshot = new SqlCommand(InsertSnapshotSql, tenant.Connection, transaction);
            insertSnapshot.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = snapshot.Id.Value });
            insertSnapshot.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = snapshot.Connector.Value });
            insertSnapshot.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = snapshot.Tenant.Value });
            insertSnapshot.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = snapshot.Project.Value });
            insertSnapshot.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = snapshot.Version });
            insertSnapshot.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = snapshot.SnapshotHash.Value });
            insertSnapshot.Parameters.Add(new SqlParameter("@count", SqlDbType.Int) { Value = snapshot.Archives.Count });
            insertSnapshot.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = snapshot.Correlation.Value });
            insertSnapshot.Parameters.Add(
                new SqlParameter("@collectedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(snapshot.CollectedAtUtc) });
            await insertSnapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var archive in snapshot.Archives)
            {
                await using var insertArchive = new SqlCommand(InsertArchiveSql, tenant.Connection, transaction);
                insertArchive.Parameters.Add(new SqlParameter("@snapshot", SqlDbType.UniqueIdentifier) { Value = snapshot.Id.Value });
                insertArchive.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = snapshot.Tenant.Value });
                insertArchive.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = snapshot.Project.Value });
                insertArchive.Parameters.Add(
                    new SqlParameter("@externalId", SqlDbType.NVarChar, 300) { Value = archive.ExternalArchiveId });
                insertArchive.Parameters.Add(new SqlParameter("@type", SqlDbType.NVarChar, 50) { Value = archive.ArchiveType });
                insertArchive.Parameters.Add(new SqlParameter("@vaultStore", SqlDbType.NVarChar, 200)
                {
                    Value = (object?)archive.VaultStoreName ?? DBNull.Value,
                });
                insertArchive.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)archive.Status });
                insertArchive.Parameters.Add(new SqlParameter("@diagnostics", SqlDbType.NVarChar, 1000)
                {
                    // Codec lossless e sem ambiguidade (AB-4C-004): CapabilityDiagnostics já chega aqui
                    // canonicamente ordenado/deduplicado pelo construtor de InventoryArchiveRecord.
                    Value = InventoryArchiveRecord.EncodeCapabilityDiagnostics(archive.CapabilityDiagnostics),
                });
                await insertArchive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new InventorySnapshotAppendResult(snapshot, Created: true);
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627)
        {
            // Colisão de versão: outra submissão concorrente do MESMO connector já gravou esta versão. Isso
            // NÃO significa, por si só, que é um réplay — duas submissões concorrentes com conteúdo
            // DIFERENTE podem calcular a mesma próxima versão (AB-4C-002). Compara semanticamente o
            // snapshot já persistido com o candidate: só converge (Created=false) se forem o MESMO
            // conteúdo (mesmo SnapshotHash). Caso contrário, a versão foi legitimamente ocupada por outra
            // mudança real — sinaliza concorrência explícita (nunca reprova o adapter, nunca perde a
            // mudança silenciosamente) para o chamador reler o latest e tentar de novo com a próxima versão
            // disponível.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var existing = await GetByVersionAsync(scope, snapshot.Connector, snapshot.Version, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            if (existing.SnapshotHash == snapshot.SnapshotHash)
            {
                return new InventorySnapshotAppendResult(existing, Created: false);
            }

            throw new ConcurrencyException(
                $"Connector {snapshot.Connector.Value}: a versão {snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                "já foi ocupada por outra submissão concorrente com conteúdo diferente. Releia o latest e tente novamente.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InventorySnapshot?> GetByVersionAsync(
        TenantScope scope, ConnectorId connector, int version, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectSnapshotByConnectorVersionSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });

        var header = await ReadSnapshotHeaderAsync(command, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var archives = await ReadArchivesAsync(tenant.Connection, null, header.Value.Id, cancellationToken).ConfigureAwait(false);
        return InventorySnapshot.Rehydrate(
            header.Value.Id, header.Value.Connector, header.Value.Tenant, header.Value.Project, header.Value.Version,
            header.Value.SnapshotHash, header.Value.ArchiveCount, archives, header.Value.Correlation, header.Value.CollectedAtUtc);
    }

    private static async Task<SnapshotHeader?> ReadSnapshotHeaderAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SnapshotHeader(
            new InventorySnapshotId(reader.GetGuid(0)),
            new ConnectorId(reader.GetGuid(1)),
            new TenantId(reader.GetGuid(2)),
            new ProjectId(reader.GetGuid(3)),
            reader.GetInt32(4),
            new Sha256Hash(reader.GetString(5)),
            reader.GetInt32(6),
            new CorrelationId(reader.GetGuid(7)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(8)));
    }

    private static async Task<IReadOnlyList<InventoryArchiveRecord>> ReadArchivesAsync(
        SqlConnection connection, SqlTransaction? transaction, InventorySnapshotId snapshotId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SelectArchivesSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@snapshot", SqlDbType.UniqueIdentifier) { Value = snapshotId.Value });

        var archives = new List<InventoryArchiveRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var externalId = reader.GetString(0);
            // Codec lossless e sem ambiguidade (AB-4C-004): DecodeCapabilityDiagnostics NÃO remove entradas
            // vazias — um payload persistido malformado (ex.: delimitador duplicado) produz uma entrada
            // vazia que o construtor abaixo recusa via TextValue.Require, em vez de ser silenciosamente
            // tratada como um código de diagnóstico válido.
            var diagnostics = InventoryArchiveRecord.DecodeCapabilityDiagnostics(reader.GetString(4));

            try
            {
                archives.Add(new InventoryArchiveRecord(
                    externalId,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    (InventoryArchiveStatus)reader.GetByte(3),
                    diagnostics));
            }
            catch (ArgumentException ex)
            {
                // A leitura é fronteira NÃO CONFIÁVEL (mesmo princípio de InventorySnapshot.Rehydrate,
                // AB-4C-003): um archive filho persistido que não reconstrói como um InventoryArchiveRecord
                // válido (ex.: capability_diagnostics corrompido) nunca é devolvido como evidência canônica.
                throw new InventorySnapshotIntegrityViolationException(
                    $"Archive {externalId} do snapshot {snapshotId.Value} não pôde ser reidratado a partir da " +
                    "linha persistida — payload malformado ou corrompido.", ex);
            }
        }

        return archives;
    }

    private readonly record struct SnapshotHeader(
        InventorySnapshotId Id,
        ConnectorId Connector,
        TenantId Tenant,
        ProjectId Project,
        int Version,
        Sha256Hash SnapshotHash,
        int ArchiveCount,
        CorrelationId Correlation,
        DateTimeOffset CollectedAtUtc);
}
