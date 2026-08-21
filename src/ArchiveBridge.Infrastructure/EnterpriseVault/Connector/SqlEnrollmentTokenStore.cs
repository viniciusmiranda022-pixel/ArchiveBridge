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
/// Store SQL de enrollment tokens (§15.1). <see cref="RedeemAsync"/> é uma das DUAS operações
/// cross-tenant aprovadas neste bounded context (a outra é a enumeração de escopos de descoberta,
/// <c>SqlEvDiscoveryPendingScopeReader</c>): o resgate precisa localizar o token SOMENTE pelo hash do
/// segredo apresentado — o tenant/projeto ainda não são conhecidos nesse instante (é o PRÓPRIO token quem
/// os determina). A identidade de MANUTENÇÃO é usada estritamente SOMENTE LEITURA (mesma restrição de
/// <c>SqlEvDiscoveryPendingScopeReader</c>: nunca grava efeito de negócio) para localizar o token e
/// resolver o escopo; o CONSUMO em si é um <c>UPDATE</c> CONDICIONAL (<c>WHERE status = Issued</c>) sob a
/// identidade normal do tenant já resolvido — o backstop de "uso único" sob corrida é o rowcount do UPDATE
/// condicional, não um lock mantido através da fronteira de manutenção. <see cref="IssueAsync"/> e
/// <see cref="RevokeAsync"/> já conhecem o escopo (o operador autenticado o define) e usam a identidade
/// tenant-scoped normal do início ao fim.
/// </summary>
public sealed class SqlEnrollmentTokenStore(TenantConnectionFactory connectionFactory) : IEnrollmentTokenStore
{
    private const string InsertSql =
        """
        INSERT INTO dbo.ev_connector_enrollment_tokens
            (token_id, tenant_id, project_id, token_hash, requested_by, correlation_id, issued_at_utc,
             expires_at_utc, status, consumed_at_utc, consumed_by_connector_id)
        VALUES (@id, @tenant, @project, @hash, @requestedBy, @correlation, @issued, @expires, 0, NULL, NULL);
        """;

    private const string SelectByHashSql =
        """
        SELECT token_id, tenant_id, project_id, token_hash, requested_by, correlation_id, issued_at_utc,
               expires_at_utc, status, consumed_at_utc, consumed_by_connector_id
        FROM dbo.ev_connector_enrollment_tokens
        WHERE token_hash = @hash;
        """;

    // UPDATE CONDICIONAL: só afeta uma linha se o token ainda estiver Issued (0) no instante da escrita —
    // é a autoridade final de "uso único" (o rowcount decide, não a leitura anterior, que pode estar
    // desatualizada sob corrida).
    private const string ConsumeIfIssuedSql =
        """
        UPDATE dbo.ev_connector_enrollment_tokens
        SET status = 1, consumed_at_utc = @consumedAt, consumed_by_connector_id = @connector
        WHERE token_id = @id AND status = 0;
        """;

    private const string SelectByIdScopedSql =
        """
        SELECT token_id, tenant_id, project_id, token_hash, requested_by, correlation_id, issued_at_utc,
               expires_at_utc, status, consumed_at_utc, consumed_by_connector_id
        FROM dbo.ev_connector_enrollment_tokens WITH (UPDLOCK, HOLDLOCK)
        WHERE token_id = @id AND project_id = @project;
        """;

    private const string RevokeSql = "UPDATE dbo.ev_connector_enrollment_tokens SET status = 2 WHERE token_id = @id;";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task IssueAsync(EnrollmentToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var scope = new TenantScope(token.Tenant, token.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = token.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = token.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = token.Project.Value });
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = token.TokenHash.Value });
        command.Parameters.Add(new SqlParameter("@requestedBy", SqlDbType.NVarChar, 200) { Value = token.RequestedBy });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = token.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@issued", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(token.IssuedAtUtc) });
        command.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(token.ExpiresAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EnrollmentToken> RedeemAsync(
        Sha256Hash tokenHash, ConnectorId connector, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        EnrollmentToken found;
        await using (var maintenance = await _connectionFactory.OpenForMaintenanceAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var select = new SqlCommand(SelectByHashSql, maintenance.Connection);
            select.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = tokenHash.Value });
            found = await ReadSingleAsync(select, cancellationToken).ConfigureAwait(false)
                ?? throw new EnrollmentTokenNotFoundException();
        }

        // Fail-closed segundo esta leitura: expirado/consumido/revogado lança ANTES de qualquer escrita.
        // Sob corrida esta leitura pode estar desatualizada — o UPDATE condicional abaixo é a autoridade final.
        var consumed = found.Consume(connector, nowUtc);
        var scope = new TenantScope(found.Tenant, found.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var update = new SqlCommand(ConsumeIfIssuedSql, tenant.Connection);
        update.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = consumed.Id.Value });
        update.Parameters.Add(
            new SqlParameter("@consumedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(consumed.ConsumedAtUtc!.Value) });
        update.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        var rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows == 0)
        {
            // Corrida perdida: outro resgate concorrente já consumiu/revogou o token entre a leitura de
            // manutenção e este UPDATE. Relê sob o escopo já conhecido para reportar o estado real.
            await using var recheck = new SqlCommand(SelectByIdScopedSql, tenant.Connection);
            recheck.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = consumed.Id.Value });
            recheck.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            var current = await ReadSingleAsync(recheck, cancellationToken).ConfigureAwait(false)
                ?? throw new EnrollmentTokenNotFoundException();
            throw new EnrollmentTokenNotConsumableException(current.Status);
        }

        return consumed;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(TenantScope scope, EnrollmentTokenId id, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var select = new SqlCommand(SelectByIdScopedSql, tenant.Connection, transaction);
            select.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id.Value });
            select.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            var existing = await ReadSingleAsync(select, cancellationToken).ConfigureAwait(false)
                ?? throw new EnrollmentTokenNotFoundException();

            // Valida a transição (consumido não pode ser revogado); revogar um já-revogado é idempotente.
            existing.Revoke();

            await using var update = new SqlCommand(RevokeSql, tenant.Connection, transaction);
            update.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id.Value });
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<EnrollmentToken?> ReadSingleAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return EnrollmentToken.Rehydrate(
            new EnrollmentTokenId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new Sha256Hash(reader.GetString(3)),
            reader.GetString(4),
            new CorrelationId(reader.GetGuid(5)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(6)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(7)),
            (EnrollmentTokenStatus)reader.GetByte(8),
            reader.IsDBNull(9) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(9)),
            reader.IsDBNull(10) ? null : new ConnectorId(reader.GetGuid(10)));
    }
}
