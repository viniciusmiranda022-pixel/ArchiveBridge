using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Delta;

/// <summary>
/// Store SQL do plano de freeze/cutover (AB-4C-008 req 9-11): UMA linha MUTÁVEL de estado atual por
/// archive, sob concorrência OTIMISTA por <c>version</c>. <see cref="SaveAsync"/> faz INSERT quando
/// <paramref name="expectedPreviousVersion"/> é <c>0</c> (nenhuma linha ainda) ou UPDATE condicionado a
/// <c>version = @expectedPreviousVersion</c> caso contrário — zero linhas afetadas (ou colisão de chave no
/// INSERT) é SEMPRE tratado como <see cref="ConcurrencyException"/>, nunca como sucesso silencioso.
/// </summary>
public sealed class SqlEvFreezePlanStore(TenantConnectionFactory connectionFactory, IClock clock) : IEvFreezePlanStore
{
    private const string Columns =
        "plan_id, connector_id, external_archive_id, status, version, authorized_by, authorized_role, " +
        "justification, authorization_correlation_id, authorized_at_utc";

    private const string SelectSql =
        $"""
        SELECT {Columns}
        FROM dbo.ev_freeze_plans
        WHERE tenant_id = @tenant AND project_id = @project AND connector_id = @connector AND external_archive_id = @archiveId;
        """;

    private const string InsertSql =
        $"""
        INSERT INTO dbo.ev_freeze_plans
            (plan_id, tenant_id, project_id, connector_id, external_archive_id, status, version, authorized_by,
             authorized_role, justification, authorization_correlation_id, authorized_at_utc, created_at_utc, updated_at_utc)
        VALUES
            (@id, @tenant, @project, @connector, @archiveId, @status, @version, @authorizedBy, @authorizedRole,
             @justification, @correlation, @authorizedAt, @now, @now);
        """;

    private const string UpdateSql =
        """
        UPDATE dbo.ev_freeze_plans
        SET status = @status, version = @version, authorized_by = @authorizedBy, authorized_role = @authorizedRole,
            justification = @justification, authorization_correlation_id = @correlation, authorized_at_utc = @authorizedAt,
            updated_at_utc = @now
        WHERE plan_id = @id AND tenant_id = @tenant AND project_id = @project AND version = @expectedPreviousVersion;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<EvFreezePlan?> GetAsync(TenantScope scope, ConnectorId connector, string externalArchiveId, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = externalArchiveId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPlan(reader, scope) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(TenantScope scope, EvFreezePlan plan, int expectedPreviousVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        if (expectedPreviousVersion == 0)
        {
            await using var insert = new SqlCommand(InsertSql, tenant.Connection);
            BindPlan(insert, scope, plan);
            insert.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(_clock.UtcNow) });
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException sql) when (sql.Number is 2601 or 2627)
            {
                throw new ConcurrencyException(
                    "Já existe um plano de freeze para este archive — releia antes de tentar novamente.", sql);
            }

            return;
        }

        await using var update = new SqlCommand(UpdateSql, tenant.Connection);
        BindPlan(update, scope, plan);
        update.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(_clock.UtcNow) });
        update.Parameters.Add(new SqlParameter("@expectedPreviousVersion", SqlDbType.Int) { Value = expectedPreviousVersion });
        var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new ConcurrencyException(
                $"Plano de freeze {plan.Id.Value}: a versão esperada ({expectedPreviousVersion}) não corresponde à persistida — alteração concorrente.");
        }
    }

    private static void BindPlan(SqlCommand command, TenantScope scope, EvFreezePlan plan)
    {
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = plan.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = plan.Connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = plan.ExternalArchiveId });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)plan.Status });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = plan.Version });
        command.Parameters.Add(new SqlParameter("@authorizedBy", SqlDbType.NVarChar, 200)
        {
            Value = (object?)plan.Authorization?.AuthorizedBy ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@authorizedRole", SqlDbType.TinyInt)
        {
            Value = plan.Authorization is { } authorization ? (byte)authorization.Role : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@justification", SqlDbType.NVarChar, 2000)
        {
            Value = (object?)plan.Authorization?.Justification ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier)
        {
            Value = plan.Authorization is { } auth ? auth.Correlation.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@authorizedAt", SqlDbType.DateTime2)
        {
            Value = plan.Authorization is { } a ? SqlJobMapping.ToDbUtc(a.AuthorizedAtUtc) : DBNull.Value,
        });
    }

    private static EvFreezePlan ReadPlan(SqlDataReader reader, TenantScope scope)
    {
        var authorizedBy = reader.IsDBNull(5) ? null : reader.GetString(5);
        EvFreezeAuthorization? authorization = authorizedBy is null
            ? null
            : new EvFreezeAuthorization(
                authorizedBy,
                (EvFreezeAuthorizationRole)reader.GetByte(6),
                reader.GetString(7),
                new CorrelationId(reader.GetGuid(8)),
                SqlJobMapping.ReadUtc(reader.GetDateTime(9)));

        return EvFreezePlan.Rehydrate(
            new FreezePlanId(reader.GetGuid(0)),
            scope.Tenant,
            scope.Project,
            new ConnectorId(reader.GetGuid(1)),
            reader.GetString(2),
            (EvFreezeStatus)reader.GetByte(3),
            authorization,
            reader.GetInt32(4));
    }
}
