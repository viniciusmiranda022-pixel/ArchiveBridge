using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Connector;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Connector;

/// <summary>
/// Store SQL da identidade durável de connectors. O registro é IDEMPOTENTE por (tenant, projeto,
/// thumbprint): sob lock (<c>UPDLOCK, HOLDLOCK</c>), procura uma identidade existente para o thumbprint —
/// se houver, converge (UPDATE dos campos operacionais via <see cref="ConnectorIdentity.ReRegister"/>,
/// fail-closed se revogada); senão insere. Uma corrida perdida contra outra instalação concorrente do
/// MESMO thumbprint (violação da constraint única) é reconciliada relendo na próxima tentativa — nunca
/// duplica identidade nem falha por corrida transitória.
/// </summary>
public sealed class SqlConnectorRegistry(TenantConnectionFactory connectionFactory) : IConnectorRegistry
{
    private const int MaxRaceRetries = 3;

    private const string SelectByThumbprintSql =
        """
        SELECT connector_id, tenant_id, project_id, thumbprint, hostname, site_name, connector_version,
               status, enrolled_via_token_id, registered_at_utc, updated_at_utc
        FROM dbo.ev_connectors WITH (UPDLOCK, HOLDLOCK)
        WHERE project_id = @project AND thumbprint = @thumbprint;
        """;

    private const string InsertSql =
        """
        INSERT INTO dbo.ev_connectors
            (connector_id, tenant_id, project_id, thumbprint, hostname, site_name, connector_version,
             status, enrolled_via_token_id, registered_at_utc, updated_at_utc)
        VALUES (@id, @tenant, @project, @thumbprint, @hostname, @site, @version, @status, @token, @registered, @updated);
        """;

    private const string UpdateSql =
        """
        UPDATE dbo.ev_connectors
        SET hostname = @hostname, site_name = @site, connector_version = @version, status = @status,
            enrolled_via_token_id = @token, updated_at_utc = @updated
        WHERE connector_id = @id;
        """;

    private const string SelectByIdSql =
        """
        SELECT connector_id, tenant_id, project_id, thumbprint, hostname, site_name, connector_version,
               status, enrolled_via_token_id, registered_at_utc, updated_at_utc
        FROM dbo.ev_connectors
        WHERE connector_id = @id AND project_id = @project;
        """;

    // Revogado (1) é terminal e idempotente: revogar duas vezes só atualiza updated_at_utc de novo.
    private const string RevokeSql =
        """
        UPDATE dbo.ev_connectors
        SET status = 1, updated_at_utc = @updated
        WHERE connector_id = @id AND tenant_id = @tenant AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<ConnectorRegistrationResult> RegisterAsync(ConnectorIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var scope = new TenantScope(identity.Tenant, identity.Project);

        for (var attempt = 0; attempt < MaxRaceRetries; attempt++)
        {
            await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
            await using var transaction =
                (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await FindByThumbprintAsync(tenant.Connection, transaction, identity, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    // Fail-closed se revogado (ConnectorRevokedException) — reinstalação nunca reativa silenciosamente.
                    var reRegistered = existing.ReRegister(
                        identity.Hostname, identity.SiteName, identity.ConnectorVersion, identity.EnrolledViaToken, identity.UpdatedAtUtc);

                    await using var update = new SqlCommand(UpdateSql, tenant.Connection, transaction);
                    update.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = reRegistered.Id.Value });
                    update.Parameters.Add(new SqlParameter("@hostname", SqlDbType.NVarChar, 260) { Value = reRegistered.Hostname });
                    update.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = reRegistered.SiteName });
                    update.Parameters.Add(new SqlParameter("@version", SqlDbType.NVarChar, 50) { Value = reRegistered.ConnectorVersion });
                    update.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)reRegistered.Status });
                    update.Parameters.Add(
                        new SqlParameter("@token", SqlDbType.UniqueIdentifier) { Value = reRegistered.EnrolledViaToken.Value });
                    update.Parameters.Add(
                        new SqlParameter("@updated", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(reRegistered.UpdatedAtUtc) });
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ConnectorRegistrationResult(reRegistered, Created: false);
                }

                await using var insert = new SqlCommand(InsertSql, tenant.Connection, transaction);
                insert.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = identity.Id.Value });
                insert.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = identity.Tenant.Value });
                insert.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = identity.Project.Value });
                insert.Parameters.Add(new SqlParameter("@thumbprint", SqlDbType.Char, 64) { Value = identity.Thumbprint.Value });
                insert.Parameters.Add(new SqlParameter("@hostname", SqlDbType.NVarChar, 260) { Value = identity.Hostname });
                insert.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = identity.SiteName });
                insert.Parameters.Add(new SqlParameter("@version", SqlDbType.NVarChar, 50) { Value = identity.ConnectorVersion });
                insert.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)identity.Status });
                insert.Parameters.Add(
                    new SqlParameter("@token", SqlDbType.UniqueIdentifier) { Value = identity.EnrolledViaToken.Value });
                insert.Parameters.Add(
                    new SqlParameter("@registered", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(identity.RegisteredAtUtc) });
                insert.Parameters.Add(
                    new SqlParameter("@updated", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(identity.UpdatedAtUtc) });
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ConnectorRegistrationResult(identity, Created: true);
            }
            catch (SqlException sql) when (sql.Number is 2601 or 2627)
            {
                // Corrida perdida contra outra instalação concorrente do MESMO thumbprint: reconcilia
                // relendo na próxima tentativa em vez de falhar ou duplicar identidade.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException("Não foi possível registrar o connector após múltiplas tentativas concorrentes.");
    }

    /// <inheritdoc />
    public async Task<ConnectorIdentity?> GetAsync(TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectByIdSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRow(reader);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(TenantScope scope, ConnectorId connector, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RevokeSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@updated", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(nowUtc) });
        var rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new ConnectorNotFoundException();
        }
    }

    private static async Task<ConnectorIdentity?> FindByThumbprintAsync(
        SqlConnection connection, SqlTransaction transaction, ConnectorIdentity identity, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(SelectByThumbprintSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = identity.Project.Value });
        command.Parameters.Add(new SqlParameter("@thumbprint", SqlDbType.Char, 64) { Value = identity.Thumbprint.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRow(reader);
    }

    private static ConnectorIdentity ReadRow(SqlDataReader reader) =>
        ConnectorIdentity.Rehydrate(
            new ConnectorId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new ConnectorPublicKeyThumbprint(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            (ConnectorRegistrationStatus)reader.GetByte(7),
            new EnrollmentTokenId(reader.GetGuid(8)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(9)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(10)));
}
