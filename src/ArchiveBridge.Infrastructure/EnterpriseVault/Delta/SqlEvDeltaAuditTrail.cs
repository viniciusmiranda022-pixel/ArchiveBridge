using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Delta;

/// <summary>
/// Store SQL append-only de custódia/auditoria de delta/freeze (AB-4C-008 req 15). Nenhuma linha é
/// atualizada ou removida.
/// </summary>
public sealed class SqlEvDeltaAuditTrail(TenantConnectionFactory connectionFactory) : IEvDeltaAuditTrail
{
    private const string InsertSql =
        """
        INSERT INTO dbo.ev_delta_events (event_id, tenant_id, project_id, run_id, watermark_id, freeze_plan_id, event_code, detail, correlation_id, occurred_at_utc)
        VALUES (@id, @tenant, @project, @run, @watermark, @freezePlan, @code, @detail, @correlation, @occurredAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AppendAsync(TenantScope scope, EvDeltaAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier)
        {
            Value = auditEvent.Run is { } run ? run.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@watermark", SqlDbType.UniqueIdentifier)
        {
            Value = auditEvent.Watermark is { } watermark ? watermark.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@freezePlan", SqlDbType.UniqueIdentifier)
        {
            Value = auditEvent.FreezePlan is { } freezePlan ? freezePlan.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@code", SqlDbType.TinyInt) { Value = (byte)auditEvent.EventCode });
        command.Parameters.Add(new SqlParameter("@detail", SqlDbType.NVarChar, 300) { Value = (object?)auditEvent.Detail ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = auditEvent.Correlation.Value });
        command.Parameters.Add(
            new SqlParameter("@occurredAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(auditEvent.OccurredAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
