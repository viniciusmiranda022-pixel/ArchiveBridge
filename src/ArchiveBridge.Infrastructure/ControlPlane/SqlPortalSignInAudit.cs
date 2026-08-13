using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Auditoria de autenticação do portal em SQL Server (append-only). Registra toda tentativa — sucesso e
/// falha — com um motivo curto e não sensível; nunca grava senha, hash ou segredo. A LEITURA é escopada
/// por tenant: como a tabela não está sob a RLS por tenant, o filtro explícito <c>tenant_id = @tenant</c>
/// é o que impede vazamento cross-tenant. Usa a identidade da aplicação por conexão simples.
/// </summary>
public sealed class SqlPortalSignInAudit(string connectionString) : IPortalSignInAudit
{
    private const string InsertSql =
        """
        INSERT INTO dbo.portal_sign_in_events
            (tenant_id, project_id, user_id, username, succeeded, reason, remote_address, correlation_id, occurred_at_utc)
        VALUES (@tenant, @project, @user, @username, @succeeded, @reason, @remote, @correlation, @occurred);
        """;

    private const string RecentSql =
        """
        SELECT TOP (@max) tenant_id, project_id, user_id, username, succeeded, reason, remote_address,
               correlation_id, occurred_at_utc
        FROM dbo.portal_sign_in_events
        WHERE tenant_id = @tenant
        ORDER BY occurred_at_utc DESC, event_id DESC;
        """;

    private readonly string _connectionString = connectionString;

    /// <inheritdoc />
    public async Task RecordAsync(PortalSignInEvent signInEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signInEvent);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = (object?)signInEvent.TenantId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = (object?)signInEvent.ProjectId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@user", SqlDbType.UniqueIdentifier) { Value = (object?)signInEvent.UserId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = signInEvent.Username });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit) { Value = signInEvent.Succeeded });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100) { Value = signInEvent.Reason });
        command.Parameters.Add(new SqlParameter("@remote", SqlDbType.NVarChar, 100) { Value = (object?)signInEvent.RemoteAddress ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = signInEvent.CorrelationId });
        command.Parameters.Add(new SqlParameter("@occurred", SqlDbType.DateTime2) { Value = signInEvent.OccurredAtUtc.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalSignInEvent>> RecentAsync(TenantId tenant, int max, CancellationToken cancellationToken)
    {
        var boundedMax = Math.Clamp(max, 1, 500);
        var events = new List<PortalSignInEvent>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RecentSql, connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = boundedMax });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new PortalSignInEvent(
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetGuid(7),
                SqlJobMapping.ReadUtc(reader.GetDateTime(8))));
        }

        return events;
    }
}
