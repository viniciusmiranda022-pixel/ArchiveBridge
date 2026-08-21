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
/// Store SQL de throttling RECUPERÁVEL (AB-4C-005 item 4; AB-4C-007 blocker 1). Cada slot
/// (<c>dbo.ev_export_connector_throttle_slots</c>/<c>_archive_throttle_slots</c>) é uma linha MUTÁVEL de
/// "propriedade atual" com deadline durável — nunca um mutex órfão permanente. A aquisição é um UPSERT
/// condicional atômico por slot (livre OU expirado ⇒ sucede; ainda válido de outro titular ⇒ falha), com os
/// DOIS slots cobertos pela MESMA transação: se qualquer um falhar, a transação inteira é revertida (nunca
/// uma aquisição parcial). O <c>IF NOT EXISTS (...) WITH (UPDLOCK, HOLDLOCK)</c> seguido do UPDATE
/// condicional é o idiom padrão de upsert-sem-corrida do SQL Server: a checagem toma um lock de chave que é
/// retido até o commit, serializando qualquer aquisição concorrente para a MESMA chave através da mesma
/// transação — nunca duas tentativas creem simultaneamente ter adquirido o mesmo slot.
/// </summary>
public sealed class SqlEvExportThrottleLeaseStore(TenantConnectionFactory connectionFactory, IClock clock) : IEvExportThrottleLeaseStore
{
    // Upsert condicional do slot de CONNECTOR: livre (lease_id IS NULL) ou expirado (expires_at_utc < @now)
    // ⇒ adquire; ainda válido de outro titular ⇒ 0 linhas afetadas (recusado). O IF NOT EXISTS com
    // UPDLOCK/HOLDLOCK toma o lock de chave ANTES de decidir INSERT/UPDATE, fechando a corrida clássica do
    // upsert (duas transações tentando criar a MESMA linha pela primeira vez).
    private const string AcquireConnectorSlotSql =
        """
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.ev_export_connector_throttle_slots WITH (UPDLOCK, HOLDLOCK) WHERE connector_id = @connector)
        BEGIN
            INSERT INTO dbo.ev_export_connector_throttle_slots
                (connector_id, tenant_id, project_id, lease_id, attempt_id, correlation_id, acquired_at_utc, expires_at_utc)
            VALUES (@connector, @tenant, @project, @lease, @attempt, @correlation, @now, @expires);
            SELECT 1 AS acquired;
        END
        ELSE
        BEGIN
            UPDATE dbo.ev_export_connector_throttle_slots
            SET tenant_id = @tenant, project_id = @project, lease_id = @lease, attempt_id = @attempt,
                correlation_id = @correlation, acquired_at_utc = @now, expires_at_utc = @expires
            WHERE connector_id = @connector AND (lease_id IS NULL OR expires_at_utc < @now);
            SELECT CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END AS acquired;
        END
        """;

    // Idêntico em forma ao slot de connector, chaveado por (tenant, projeto, archive) — RLS já restringe a
    // sessão ao próprio tenant, então a chave lógica completa é sempre respeitada.
    private const string AcquireArchiveSlotSql =
        """
        SET NOCOUNT ON;
        IF NOT EXISTS (
            SELECT 1 FROM dbo.ev_export_archive_throttle_slots WITH (UPDLOCK, HOLDLOCK)
            WHERE tenant_id = @tenant AND project_id = @project AND external_archive_id = @archiveId)
        BEGIN
            INSERT INTO dbo.ev_export_archive_throttle_slots
                (tenant_id, project_id, external_archive_id, lease_id, attempt_id, correlation_id, acquired_at_utc, expires_at_utc)
            VALUES (@tenant, @project, @archiveId, @lease, @attempt, @correlation, @now, @expires);
            SELECT 1 AS acquired;
        END
        ELSE
        BEGIN
            UPDATE dbo.ev_export_archive_throttle_slots
            SET lease_id = @lease, attempt_id = @attempt, correlation_id = @correlation, acquired_at_utc = @now, expires_at_utc = @expires
            WHERE tenant_id = @tenant AND project_id = @project AND external_archive_id = @archiveId
                  AND (lease_id IS NULL OR expires_at_utc < @now);
            SELECT CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END AS acquired;
        END
        """;

    // Renovação: exige o MESMO lease_id (titular) e um deadline ainda não vencido — nunca ressuscita um
    // lease já expirado, mesmo padrão de SqlJobLeaseManager.RenewSql.
    private const string RenewConnectorSlotSql =
        "UPDATE dbo.ev_export_connector_throttle_slots SET expires_at_utc = @expires " +
        "WHERE connector_id = @connector AND lease_id = @lease AND expires_at_utc > @now;";

    private const string RenewArchiveSlotSql =
        "UPDATE dbo.ev_export_archive_throttle_slots SET expires_at_utc = @expires " +
        "WHERE tenant_id = @tenant AND project_id = @project AND external_archive_id = @archiveId " +
        "AND lease_id = @lease AND expires_at_utc > @now;";

    // Liberação: idempotente — só libera se o lease_id ainda corresponde (0 linhas se já liberado, expirado
    // e reclamado por outro titular, ou nunca existiu). Nunca lança.
    private const string ReleaseConnectorSlotSql =
        "UPDATE dbo.ev_export_connector_throttle_slots " +
        "SET lease_id = NULL, attempt_id = NULL, correlation_id = NULL, acquired_at_utc = NULL, expires_at_utc = NULL " +
        "WHERE connector_id = @connector AND lease_id = @lease;";

    private const string ReleaseArchiveSlotSql =
        "UPDATE dbo.ev_export_archive_throttle_slots " +
        "SET lease_id = NULL, attempt_id = NULL, correlation_id = NULL, acquired_at_utc = NULL, expires_at_utc = NULL " +
        "WHERE tenant_id = @tenant AND project_id = @project AND external_archive_id = @archiveId AND lease_id = @lease;";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<EvExportThrottleLease?> TryAcquireAsync(
        TenantScope scope, ConnectorId connector, string externalArchiveId, ExportAttemptId attempt,
        CorrelationId correlation, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "leaseDuration deve ser positivo.");
        }

        var leaseId = Guid.NewGuid();
        var now = _clock.UtcNow;
        var expires = now + leaseDuration;

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connectorAcquired = await ExecuteAcquireAsync(
                tenant.Connection, transaction, AcquireConnectorSlotSql, scope, connector, externalArchiveId: null,
                leaseId, attempt, correlation, now, expires, cancellationToken).ConfigureAwait(false);
            if (!connectorAcquired)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null; // connector já em uso por um lease ainda válido — throttled.
            }

            var archiveAcquired = await ExecuteAcquireAsync(
                tenant.Connection, transaction, AcquireArchiveSlotSql, scope, connector: null, externalArchiveId,
                leaseId, attempt, correlation, now, expires, cancellationToken).ConfigureAwait(false);
            if (!archiveAcquired)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null; // archive já em uso por um lease ainda válido — throttled (o slot de connector é revertido).
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EvExportThrottleLease(leaseId, connector, externalArchiveId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        TenantScope scope, EvExportThrottleLease lease, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "leaseDuration deve ser positivo.");
        }

        var now = _clock.UtcNow;
        var expires = now + leaseDuration;

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int connectorRows;
            await using (var command = new SqlCommand(RenewConnectorSlotSql, tenant.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = lease.Connector.Value });
                command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = lease.LeaseId });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
                command.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(expires) });
                connectorRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int archiveRows;
            await using (var command = new SqlCommand(RenewArchiveSlotSql, tenant.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = lease.ExternalArchiveId });
                command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = lease.LeaseId });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
                command.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(expires) });
                archiveRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (connectorRows == 0 || archiveRows == 0)
            {
                // Lease expirado ou reassumido a outro titular: nenhum dos dois avança (fail-closed, sem
                // renovação parcial) — o chamador deve tratar como cercamento perdido.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(TenantScope scope, EvExportThrottleLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        await using (var command = new SqlCommand(ReleaseConnectorSlotSql, tenant.Connection))
        {
            command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = lease.Connector.Value });
            command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = lease.LeaseId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); // idempotente: 0 linhas se já liberado/reassumido.
        }

        await using (var command = new SqlCommand(ReleaseArchiveSlotSql, tenant.Connection))
        {
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = lease.ExternalArchiveId });
            command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = lease.LeaseId });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); // idempotente: 0 linhas se já liberado/reassumido.
        }
    }

    // Núcleo compartilhado da aquisição de UM slot (connector OU archive) — exatamente um de
    // connector/externalArchiveId é não-nulo, selecionando o SQL e os parâmetros correspondentes.
    private static async Task<bool> ExecuteAcquireAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, TenantScope scope,
        ConnectorId? connector, string? externalArchiveId, Guid leaseId, ExportAttemptId attempt,
        CorrelationId correlation, DateTimeOffset now, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        if (connector is { } c)
        {
            command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = c.Value });
        }

        if (externalArchiveId is not null)
        {
            command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = externalArchiveId });
        }

        command.Parameters.Add(new SqlParameter("@lease", SqlDbType.UniqueIdentifier) { Value = leaseId });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.UniqueIdentifier) { Value = attempt.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
        command.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(expires) });

        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}
