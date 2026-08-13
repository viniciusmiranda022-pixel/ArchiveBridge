using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Auditoria operacional append-only. Toda conexão é aberta com o tenant do usuário (RLS) e toda consulta
/// também filtra explicitamente por projeto. Não registra bytes de evidência, segredo ou caminho físico.
/// </summary>
public sealed class SqlPortalOperationalAudit(TenantConnectionFactory connectionFactory) : IPortalOperationalAudit
{
    private const string InsertSql =
        """
        INSERT INTO dbo.portal_operational_audit_events
            (tenant_id, project_id, user_id, username, action_code, resource_type, resource_id,
             succeeded, reason, correlation_id, occurred_at_utc)
        VALUES
            (@tenant, @project, @user, @username, @action, @resourceType, @resourceId,
             @succeeded, @reason, @correlation, @occurred);
        """;

    private const string RecentSql =
        """
        SELECT TOP (@max) user_id, username, action_code, resource_type, resource_id,
               succeeded, reason, correlation_id, occurred_at_utc
        FROM dbo.portal_operational_audit_events
        WHERE project_id = @project
        ORDER BY occurred_at_utc DESC, event_id DESC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task RecordAsync(
        TenantScope scope,
        PortalOperationalAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@user", SqlDbType.UniqueIdentifier) { Value = auditEvent.UserId });
        command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = auditEvent.Username });
        command.Parameters.Add(new SqlParameter("@action", SqlDbType.NVarChar, 64) { Value = auditEvent.ActionCode });
        command.Parameters.Add(new SqlParameter("@resourceType", SqlDbType.NVarChar, 64) { Value = auditEvent.ResourceType });
        command.Parameters.Add(new SqlParameter("@resourceId", SqlDbType.NVarChar, 200) { Value = auditEvent.ResourceId });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit) { Value = auditEvent.Succeeded });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100) { Value = auditEvent.Reason });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = auditEvent.CorrelationId });
        command.Parameters.Add(new SqlParameter("@occurred", SqlDbType.DateTime2) { Value = auditEvent.OccurredAtUtc.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalOperationalAuditEvent>> RecentAsync(
        TenantScope scope,
        int max,
        CancellationToken cancellationToken)
    {
        var events = new List<PortalOperationalAuditEvent>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RecentSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = Math.Clamp(max, 1, 500) });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new PortalOperationalAuditEvent(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetString(6),
                reader.GetGuid(7),
                SqlJobMapping.ReadUtc(reader.GetDateTime(8))));
        }

        return events;
    }
}
