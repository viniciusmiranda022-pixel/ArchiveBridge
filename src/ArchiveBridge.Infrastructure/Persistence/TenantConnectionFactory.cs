using System.Data;
using ArchiveBridge.Contracts.Jobs;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Persistence;

/// <summary>
/// Conexão com o tenant definido em <c>SESSION_CONTEXT('tenant_id')</c>. Ao ser descartada, limpa
/// o contexto antes de devolver a conexão ao pool (defesa em profundidade; o driver também executa
/// <c>sp_reset_connection</c> no reuso do pool, que zera o SESSION_CONTEXT).
/// </summary>
public sealed class TenantConnection(SqlConnection connection) : IAsyncDisposable
{
    private const string ClearContextSql =
        "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = NULL; " +
        "EXEC sys.sp_set_session_context @key = N'is_maintenance', @value = NULL;";

    /// <summary>Conexão SQL aberta com o contexto de tenant/manutenção já definido.</summary>
    public SqlConnection Connection { get; } = connection;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Connection.State == ConnectionState.Open)
            {
                await using var clear = new SqlCommand(ClearContextSql, Connection);
                await clear.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        catch (SqlException)
        {
            // Conexão possivelmente quebrada: o pool a descarta e sp_reset_connection zera o
            // contexto no próximo uso. Não há estado sensível a proteger além do próprio contexto.
        }
        finally
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Abre conexões com o contexto de tenant aplicado por unidade de trabalho. Falha de forma
/// fechada: toda operação de negócio exige um <see cref="TenantScope"/> válido; o modo de
/// manutenção (reaper) é um caminho separado e explícito.
/// </summary>
public sealed class TenantConnectionFactory(string connectionString)
{
    private const string SetTenantSql =
        "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 0;";

    private const string SetMaintenanceSql =
        "EXEC sys.sp_set_session_context @key = N'is_maintenance', @value = 1, @read_only = 0;";

    private readonly string _connectionString = connectionString;

    /// <summary>Abre uma conexão com <c>SESSION_CONTEXT('tenant_id')</c> = tenant do escopo.</summary>
    public async Task<TenantConnection> OpenForTenantAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(SetTenantSql, connection);
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new TenantConnection(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Abre uma conexão em modo de manutenção (<c>is_maintenance = 1</c>) para o reaper de leases
    /// expirados. É o único caminho autorizado a enxergar múltiplos tenants, de forma controlada.
    /// </summary>
    public async Task<TenantConnection> OpenForMaintenanceAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(SetMaintenanceSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new TenantConnection(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Abre uma conexão administrativa sem contexto de tenant (usada por migrations/DDL).</summary>
    public async Task<SqlConnection> OpenAdminAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
