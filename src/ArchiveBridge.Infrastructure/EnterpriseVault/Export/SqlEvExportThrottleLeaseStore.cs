using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Export;

/// <summary>
/// Store SQL de throttling (AB-4C-005 item 4): UM único <c>INSERT</c> por tentativa, protegido pelos DOIS
/// índices únicos FILTRADOS (<c>UX_ev_export_throttle_leases_connector</c>/<c>_archive</c>, WHERE
/// <c>released_at_utc IS NULL</c>) — se QUALQUER um já estiver em uso, o INSERT falha por violação de
/// unicidade e a aquisição é recusada por inteiro (nunca uma aquisição parcial).
/// </summary>
public sealed class SqlEvExportThrottleLeaseStore(TenantConnectionFactory connectionFactory, IClock clock) : IEvExportThrottleLeaseStore
{
    private const int UniqueViolation = 2601;
    private const int PrimaryKeyViolation = 2627;

    private const string AcquireSql =
        """
        INSERT INTO dbo.ev_export_throttle_leases
            (lease_id, tenant_id, project_id, connector_id, external_archive_id, attempt_id, correlation_id, acquired_at_utc, released_at_utc)
        VALUES (@lease, @tenant, @project, @connector, @archiveId, @attempt, @correlation, @now, NULL);
        """;

    private const string ReleaseSql =
        "UPDATE dbo.ev_export_throttle_leases SET released_at_utc = @now " +
        "WHERE lease_id = @lease AND tenant_id = @tenant AND project_id = @project AND released_at_utc IS NULL;";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<EvExportThrottleLease?> TryAcquireAsync(
        TenantScope scope, ConnectorId connector, string externalArchiveId, ExportAttemptId attempt,
        CorrelationId correlation, CancellationToken cancellationToken)
    {
        var leaseId = Guid.NewGuid();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(AcquireSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = leaseId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = externalArchiveId });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.UniqueIdentifier) { Value = attempt.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(_clock.UtcNow) });

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number is UniqueViolation or PrimaryKeyViolation)
        {
            return null; // connector ou archive já em uso — throttled.
        }

        return new EvExportThrottleLease(leaseId, connector, externalArchiveId);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(TenantScope scope, EvExportThrottleLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(ReleaseSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = lease.LeaseId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(_clock.UtcNow) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); // idempotente: 0 linhas se já liberado.
    }
}
