using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Leitura da trilha de custódia/auditoria de exportação (<c>dbo.ev_export_events</c>) dentro do escopo de
/// tenant (RLS aplicada). Somente ids/códigos/detalhe curto são expostos — nunca conteúdo sensível (a
/// trilha nunca os contém).
/// </summary>
public sealed class SqlEvExportAuditReader(TenantConnectionFactory connectionFactory) : IEvExportAuditReader
{
    private const string SelectSql =
        """
        SELECT attempt_id, event_code, detail, correlation_id, occurred_at_utc
        FROM dbo.ev_export_events
        WHERE request_id = @request AND tenant_id = @tenant AND project_id = @project
        ORDER BY event_id ASC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvExportAuditEvent>> GetEventsAsync(
        TenantScope scope, ExportRequestId request, CancellationToken cancellationToken)
    {
        var events = new List<EvExportAuditEvent>();

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@request", SqlDbType.UniqueIdentifier) { Value = request.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new EvExportAuditEvent(
                request,
                reader.IsDBNull(0) ? null : new ExportAttemptId(reader.GetGuid(0)),
                (EvExportAuditEventCode)reader.GetByte(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                new CorrelationId(reader.GetGuid(3)),
                SqlJobMapping.ReadUtc(reader.GetDateTime(4))));
        }

        return events;
    }
}
