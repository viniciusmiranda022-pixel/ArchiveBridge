using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Store SQL append-only de custódia/auditoria de exportação (AB-4C-005 item 17). Nenhuma linha é
/// atualizada ou removida.
/// </summary>
public sealed class SqlEvExportAuditTrail(TenantConnectionFactory connectionFactory) : IEvExportAuditTrail
{
    private const string InsertSql =
        """
        INSERT INTO dbo.ev_export_events (event_id, request_id, tenant_id, project_id, attempt_id, event_code, detail, correlation_id, occurred_at_utc)
        VALUES (@id, @request, @tenant, @project, @attempt, @code, @detail, @correlation, @occurredAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AppendAsync(TenantScope scope, EvExportAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@request", SqlDbType.UniqueIdentifier) { Value = auditEvent.Request.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.UniqueIdentifier)
        {
            Value = auditEvent.Attempt is { } attempt ? attempt.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@code", SqlDbType.TinyInt) { Value = (byte)auditEvent.EventCode });
        command.Parameters.Add(new SqlParameter("@detail", SqlDbType.NVarChar, 300) { Value = (object?)auditEvent.Detail ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = auditEvent.Correlation.Value });
        command.Parameters.Add(
            new SqlParameter("@occurredAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(auditEvent.OccurredAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
