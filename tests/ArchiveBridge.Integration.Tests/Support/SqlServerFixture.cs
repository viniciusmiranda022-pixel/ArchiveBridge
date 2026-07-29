using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>
/// Provisiona um banco de teste dedicado no SQL Server real (nada em memória) e aplica as
/// migrations uma vez. A string de conexão vem da variável de ambiente
/// <c>ARCHIVEBRIDGE_TEST_SQL</c> (nenhum segredo é versionado). Cada teste usa tenants/projetos
/// com GUIDs próprios; a RLS por tenant garante o isolamento entre eles.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string EnvironmentVariable = "ARCHIVEBRIDGE_TEST_SQL";

    private string _databaseName = string.Empty;
    private string _adminConnectionString = string.Empty;

    /// <summary>String de conexão apontando para o banco de teste desta execução.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Fábrica de conexões com contexto de tenant, ligada ao banco de teste.</summary>
    public TenantConnectionFactory Factory { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"Defina {EnvironmentVariable} com a conexão do SQL Server de teste " +
                "(ex.: 'Server=localhost,11433;User ID=sa;Password=...;TrustServerCertificate=True;Encrypt=False'). " +
                "Os testes de integração exigem SQL Server real; não usam banco em memória.");

        _databaseName = "ab_slice1_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        _adminConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }
            .ConnectionString;

        await ExecuteAdminAsync($"CREATE DATABASE [{_databaseName}];");

        builder.InitialCatalog = _databaseName;
        ConnectionString = builder.ConnectionString;

        await new MigrationRunner(ConnectionString).ApplyAsync(CancellationToken.None);
        Factory = new TenantConnectionFactory(ConnectionString);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        await ExecuteAdminAsync(
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
    }

    /// <summary>Cria um escopo de tenant/projeto único (isolado pela RLS).</summary>
    public static TenantScope NewScope() =>
        new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    /// <summary>Cria um <see cref="SqlJobStore"/> com o relógio informado.</summary>
    public SqlJobStore Store(IClock clock) => new(Factory, clock);

    /// <summary>Cria um <see cref="SqlJobLeaseManager"/> com política e duração de lease informadas.</summary>
    public SqlJobLeaseManager LeaseManager(IClock clock, RetryPolicy policy, TimeSpan leaseDuration) =>
        new(Factory, clock, policy, leaseDuration);

    /// <summary>Cria um <see cref="SqlJobAuditReader"/>.</summary>
    public SqlJobAuditReader AuditReader() => new(Factory);

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new SqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Coleção que compartilha o banco de teste e serializa os testes de integração.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>
{
    /// <summary>Nome da coleção de integração.</summary>
    public const string Name = "sql-server";
}
