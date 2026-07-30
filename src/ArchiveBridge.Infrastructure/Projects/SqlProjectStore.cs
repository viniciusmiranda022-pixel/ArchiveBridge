using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Projects;

/// <summary>
/// Persistência do agregado <see cref="MigrationProject"/> em SQL Server. Cria o projeto-base do
/// Slice 1 (integração com Jobs) e a versão de configuração inicial; transições de estado e novas
/// versões de configuração são gravadas de forma separada (a de estado nunca toca a configuração,
/// evitando disparar o gatilho de congelamento). RLS por SESSION_CONTEXT + filtro por project_id.
/// </summary>
public sealed class SqlProjectStore(TenantConnectionFactory connectionFactory) : IProjectStore
{
    private const string AddSql =
        """
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE project_id = @projectId)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@projectId, @tenant, @createdAt);

        INSERT INTO dbo.migration_projects
            (project_id, tenant_id, project_name, project_owner, target_tenant, target_archive_policy,
             configuration_version, configuration_hash, status, created_at_utc, updated_at_utc)
        VALUES
            (@projectId, @tenant, @name, @owner, @targetTenant, @policy,
             @cfgVersion, @cfgHash, @status, @createdAt, @updatedAt);

        INSERT INTO dbo.project_configuration_versions
            (project_id, tenant_id, configuration_version, target_tenant, target_archive_policy, configuration_hash, created_at_utc)
        VALUES
            (@projectId, @tenant, @cfgVersion, @targetTenant, @policy, @cfgHash, @createdAt);
        """;

    private const string GetSql =
        """
        SELECT project_id, tenant_id, project_name, project_owner, target_tenant, target_archive_policy,
               configuration_version, configuration_hash, status, created_at_utc, updated_at_utc
        FROM dbo.migration_projects
        WHERE project_id = @projectId;
        """;

    private const string SaveStatusSql =
        "UPDATE dbo.migration_projects SET status = @status, updated_at_utc = @updatedAt WHERE project_id = @projectId;";

    private const string SaveConfigurationSql =
        """
        SET NOCOUNT ON;
        UPDATE dbo.migration_projects
        SET target_tenant = @targetTenant, target_archive_policy = @policy,
            configuration_version = @cfgVersion, configuration_hash = @cfgHash,
            status = @status, updated_at_utc = @updatedAt
        WHERE project_id = @projectId;

        INSERT INTO dbo.project_configuration_versions
            (project_id, tenant_id, configuration_version, target_tenant, target_archive_policy, configuration_hash, created_at_utc)
        VALUES
            (@projectId, @tenant, @cfgVersion, @targetTenant, @policy, @cfgHash, @updatedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AddAsync(MigrationProject project, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var scope = new TenantScope(project.Tenant, project.Id);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = new SqlCommand(AddSql, connection.Connection, transaction))
            {
                BindProject(command, project);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MigrationProject?> GetAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(GetSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadProject(reader);
    }

    /// <inheritdoc />
    public async Task SaveStatusAsync(MigrationProject project, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var scope = new TenantScope(project.Tenant, project.Id);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new SqlCommand(SaveStatusSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)project.Status });
        command.Parameters.Add(new SqlParameter("@updatedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(project.UpdatedAtUtc) });
        command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = project.Id.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveConfigurationAsync(MigrationProject project, CorrelationId correlation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var scope = new TenantScope(project.Tenant, project.Id);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = new SqlCommand(SaveConfigurationSql, connection.Connection, transaction))
            {
                BindProject(command, project);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void BindProject(SqlCommand command, MigrationProject project)
    {
        command.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = project.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = project.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = project.Name.Value });
        command.Parameters.Add(new SqlParameter("@owner", SqlDbType.NVarChar, 200) { Value = project.Owner.Value });
        command.Parameters.Add(new SqlParameter("@targetTenant", SqlDbType.NVarChar, 256) { Value = project.Configuration.TargetTenant.Value });
        command.Parameters.Add(new SqlParameter("@policy", SqlDbType.TinyInt) { Value = (byte)project.Configuration.TargetArchivePolicy });
        command.Parameters.Add(new SqlParameter("@cfgVersion", SqlDbType.Int) { Value = project.ConfigurationVersion.Value });
        command.Parameters.Add(new SqlParameter("@cfgHash", SqlDbType.Char, 64) { Value = project.ConfigurationHash.Value });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)project.Status });
        command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(project.CreatedAtUtc) });
        command.Parameters.Add(new SqlParameter("@updatedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(project.UpdatedAtUtc) });
    }

    private static MigrationProject ReadProject(SqlDataReader reader)
    {
        var projectId = new ProjectId(reader.GetGuid(0));
        var tenant = new TenantId(reader.GetGuid(1));
        var name = new ProjectName(reader.GetString(2));
        var owner = new ProjectOwner(reader.GetString(3));
        var configuration = new ProjectConfiguration(
            new TargetTenant(reader.GetString(4)),
            (TargetArchivePolicy)reader.GetByte(5));
        var configurationVersion = new ConfigurationVersion(reader.GetInt32(6));
        var configurationHash = new Sha256Hash(reader.GetString(7).TrimEnd());
        var status = (ProjectStatus)reader.GetByte(8);
        var createdAt = SqlJobMapping.ReadUtc(reader.GetDateTime(9));
        var updatedAt = SqlJobMapping.ReadUtc(reader.GetDateTime(10));

        return MigrationProject.Rehydrate(
            projectId, tenant, name, owner, configuration, configurationVersion, configurationHash, status, createdAt, updatedAt);
    }
}
