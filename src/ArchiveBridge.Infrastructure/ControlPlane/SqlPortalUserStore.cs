using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Persistência dos usuários do portal em SQL Server. Usa a identidade da APLICAÇÃO por uma conexão
/// simples (as tabelas <c>portal_*</c> não estão sob a RLS por tenant — são o mecanismo que estabelece o
/// tenant do usuário). Nunca lê nem grava senha em claro: apenas o hash derivado. Criação valida papéis
/// (fail-closed) e é atômica (usuário + vínculos numa transação); login duplicado é recusado pela
/// constraint <c>UQ_portal_users_username</c>.
/// </summary>
public sealed class SqlPortalUserStore(string connectionString) : IPortalUserStore
{
    private const string CountSql = "SELECT COUNT(*) FROM dbo.portal_users;";

    private const string FindSql =
        """
        SELECT user_id, username, display_name, tenant_id, project_id, enabled,
               password_algorithm, password_iterations, password_salt, password_hash
        FROM dbo.portal_users
        WHERE username = @username;
        """;

    private const string RolesSql =
        "SELECT role_name FROM dbo.portal_user_roles WHERE user_id = @userId ORDER BY role_name ASC;";

    private const string InsertUserSql =
        """
        INSERT INTO dbo.portal_users
            (user_id, username, display_name, tenant_id, project_id, enabled,
             password_algorithm, password_iterations, password_salt, password_hash)
        VALUES
            (@userId, @username, @displayName, @tenant, @project, 1,
             @algorithm, @iterations, @salt, @hash);
        """;

    private const string InsertRoleSql =
        "INSERT INTO dbo.portal_user_roles (user_id, role_name) VALUES (@userId, @role);";

    private readonly string _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(CountSql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<PortalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        Guid userId;
        PortalUserRecord record;
        await using (var command = new SqlCommand(FindSql, connection))
        {
            command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = username });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            userId = reader.GetGuid(0);
            record = new PortalUserRecord(
                userId,
                reader.GetString(1),
                reader.GetString(2),
                new TenantId(reader.GetGuid(3)),
                new ProjectId(reader.GetGuid(4)),
                reader.GetBoolean(5),
                new PortalPasswordHash(reader.GetString(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9)),
                []);
        }

        var roles = new List<string>();
        await using (var rolesCommand = new SqlCommand(RolesSql, connection))
        {
            rolesCommand.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = userId });
            await using var reader = await rolesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                roles.Add(reader.GetString(0));
            }
        }

        return record with { Roles = roles };
    }

    /// <inheritdoc />
    public async Task CreateAsync(PortalUserRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrEmpty(registration.Username);
        if (registration.Roles.Count == 0)
        {
            throw new ArgumentException("Um usuário do portal precisa de ao menos um papel.", nameof(registration));
        }

        foreach (var role in registration.Roles)
        {
            if (!PortalRoles.IsKnown(role))
            {
                throw new ArgumentException($"Papel desconhecido: '{role}'.", nameof(registration));
            }
        }

        var userId = Guid.NewGuid();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = new SqlCommand(InsertUserSql, connection, transaction))
        {
            command.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = userId });
            command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = registration.Username });
            command.Parameters.Add(new SqlParameter("@displayName", SqlDbType.NVarChar, 200) { Value = registration.DisplayName });
            command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = registration.Tenant.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = registration.Project.Value });
            command.Parameters.Add(new SqlParameter("@algorithm", SqlDbType.NVarChar, 50) { Value = registration.Password.Algorithm });
            command.Parameters.Add(new SqlParameter("@iterations", SqlDbType.Int) { Value = registration.Password.Iterations });
            command.Parameters.Add(new SqlParameter("@salt", SqlDbType.NVarChar, 200) { Value = registration.Password.SaltBase64 });
            command.Parameters.Add(new SqlParameter("@hash", SqlDbType.NVarChar, 200) { Value = registration.Password.HashBase64 });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var role in registration.Roles.Distinct(StringComparer.Ordinal))
        {
            await using var roleCommand = new SqlCommand(InsertRoleSql, connection, transaction);
            roleCommand.Parameters.Add(new SqlParameter("@userId", SqlDbType.UniqueIdentifier) { Value = userId });
            roleCommand.Parameters.Add(new SqlParameter("@role", SqlDbType.NVarChar, 50) { Value = role });
            await roleCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
