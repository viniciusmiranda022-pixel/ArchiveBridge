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
/// Store SQL append-only de handshakes de capability/versão. Nenhuma linha é atualizada ou removida —
/// <see cref="GetLatestAsync"/> sempre lê o handshake vigente (mais recente por
/// <see cref="ConnectorCapabilityHandshake.CollectedAtUtc"/>) diretamente do histórico completo.
/// </summary>
public sealed class SqlConnectorCapabilityStore(TenantConnectionFactory connectionFactory) : IConnectorCapabilityStore
{
    private const string InsertSql =
        """
        INSERT INTO dbo.ev_connector_capability_handshakes
            (handshake_id, connector_id, tenant_id, project_id, ev_version_display,
             powershell_snapin_available, support_level, export_capable, blocking_reason, correlation_id,
             collected_at_utc)
        VALUES (@id, @connector, @tenant, @project, @version, @snapin, @level, @exportCapable, @blockingReason,
                @correlation, @collectedAt);
        """;

    private const string SelectLatestSql =
        """
        SELECT TOP (1) handshake_id, connector_id, tenant_id, project_id, ev_version_display,
               powershell_snapin_available, support_level, export_capable, blocking_reason, correlation_id,
               collected_at_utc
        FROM dbo.ev_connector_capability_handshakes
        WHERE connector_id = @connector AND project_id = @project
        ORDER BY collected_at_utc DESC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AppendAsync(ConnectorCapabilityHandshake handshake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handshake);
        var scope = new TenantScope(handshake.Tenant, handshake.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = handshake.Id.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = handshake.Connector.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = handshake.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = handshake.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.NVarChar, 100) { Value = handshake.EvVersionDisplay });
        command.Parameters.Add(new SqlParameter("@snapin", SqlDbType.Bit) { Value = handshake.PowerShellSnapinAvailable });
        command.Parameters.Add(new SqlParameter("@level", SqlDbType.TinyInt) { Value = (byte)handshake.SupportLevel });
        command.Parameters.Add(new SqlParameter("@exportCapable", SqlDbType.Bit) { Value = handshake.ExportCapable });
        command.Parameters.Add(new SqlParameter("@blockingReason", SqlDbType.NVarChar, 200)
        {
            Value = (object?)handshake.BlockingReason ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = handshake.Correlation.Value });
        command.Parameters.Add(
            new SqlParameter("@collectedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(handshake.CollectedAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConnectorCapabilityHandshake?> GetLatestAsync(
        TenantScope scope, ConnectorId connector, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectLatestSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ConnectorCapabilityHandshake.Rehydrate(
            new CapabilityHandshakeId(reader.GetGuid(0)),
            new ConnectorId(reader.GetGuid(1)),
            new TenantId(reader.GetGuid(2)),
            new ProjectId(reader.GetGuid(3)),
            reader.GetString(4),
            reader.GetBoolean(5),
            (ConnectorSupportLevel)reader.GetByte(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            new CorrelationId(reader.GetGuid(9)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(10)));
    }
}
