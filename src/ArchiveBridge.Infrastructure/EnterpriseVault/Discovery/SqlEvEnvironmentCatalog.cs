using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Resolve ambientes EV sob a identidade da aplicação com <c>SESSION_CONTEXT('tenant_id')</c> (RLS por
/// tenant) E filtro explícito por <c>project_id</c>. Um ambiente de outro tenant/projeto — ou inexistente —
/// devolve <see langword="null"/> de forma indistinguível (anti-IDOR/enumeração). O site/directory retornados
/// são os valores autoritativos do SQL, usados server-side na construção do comando de descoberta.
/// </summary>
public sealed class SqlEvEnvironmentCatalog(TenantConnectionFactory connectionFactory) : IEvEnvironmentCatalog
{
    private const string SelectSql =
        """
        SELECT site_name, directory_server
        FROM dbo.ev_environments
        WHERE environment_id = @environment AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<EvEnvironmentRecord?> GetAsync(
        TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new EvEnvironmentRecord(environmentId, reader.GetString(0), reader.GetString(1));
    }
}
