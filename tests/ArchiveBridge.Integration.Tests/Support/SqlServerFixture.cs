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
/// Provisiona um banco de teste dedicado no SQL Server real (nada em memória), aplica as migrations
/// e cria DUAS identidades concretas (usuários contidos): <c>ab_app</c> (papel da aplicação) e
/// <c>ab_reaper</c> (papel de manutenção). A string base vem de <c>ARCHIVEBRIDGE_TEST_SQL</c>
/// (nenhum segredo versionado); as senhas dos usuários contidos são aleatórias e ficam só em
/// memória. Cada teste usa tenants/projetos com GUIDs próprios.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string EnvironmentVariable = "ARCHIVEBRIDGE_TEST_SQL";
    private static readonly TimeSpan NoAging = TimeSpan.FromDays(3650);

    private string _databaseName = string.Empty;
    private string _masterConnectionString = string.Empty;

    /// <summary>Conexão com a identidade da APLICAÇÃO (ab_app) no banco de teste.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Conexão com a identidade de MANUTENÇÃO (ab_reaper) no banco de teste.</summary>
    public string MaintenanceConnectionString { get; private set; } = string.Empty;

    /// <summary>Conexão administrativa (sa) no banco de teste — usada por migrations/DDL.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Fábrica de conexões (app + manutenção) ligada ao banco de teste.</summary>
    public TenantConnectionFactory Factory { get; private set; } = null!;

    /// <summary>Raiz temporária isolada para os artefatos imutáveis de mapping (filesystem on-premises).</summary>
    public string ArtifactRoot { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        ArtifactRoot = Path.Combine(Path.GetTempPath(), "ab_artifacts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactRoot);

        var baseConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"Defina {EnvironmentVariable} com a conexão sa do SQL Server de teste " +
                "(ex.: 'Server=localhost,11433;User ID=sa;Password=...;TrustServerCertificate=True;Encrypt=False'). " +
                "Os testes de integração exigem SQL Server real; não usam banco em memória.");

        _databaseName = "ab_slice1_" + Guid.NewGuid().ToString("N");
        _masterConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }
            .ConnectionString;

        await ExecuteAsync(_masterConnectionString, "EXEC sys.sp_configure N'contained database authentication', 1; RECONFIGURE;");
        await ExecuteAsync(_masterConnectionString, $"CREATE DATABASE [{_databaseName}];");
        await ExecuteAsync(_masterConnectionString, $"ALTER DATABASE [{_databaseName}] SET CONTAINMENT = PARTIAL;");

        AdminConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = _databaseName }
            .ConnectionString;

        await new MigrationRunner(AdminConnectionString).ApplyAsync(CancellationToken.None);

        var appPassword = RandomPassword();
        var reaperPassword = RandomPassword();
        await ExecuteAsync(AdminConnectionString,
            $"CREATE USER ab_app WITH PASSWORD = '{appPassword}'; ALTER ROLE ab_app_role ADD MEMBER ab_app;");
        await ExecuteAsync(AdminConnectionString,
            $"CREATE USER ab_reaper WITH PASSWORD = '{reaperPassword}'; ALTER ROLE ab_maintenance_role ADD MEMBER ab_reaper;");

        ConnectionString = ContainedUserConnectionString(baseConnectionString, "ab_app", appPassword);
        MaintenanceConnectionString = ContainedUserConnectionString(baseConnectionString, "ab_reaper", reaperPassword);
        Factory = new TenantConnectionFactory(ConnectionString, MaintenanceConnectionString);
    }

    /// <summary>String de conexão de um usuário contido (autentica direto no banco de teste).</summary>
    private string ContainedUserConnectionString(string baseConnectionString, string user, string password) =>
        new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = _databaseName,
            UserID = user,
            Password = password,
        }.ConnectionString;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (!string.IsNullOrEmpty(ArtifactRoot) && Directory.Exists(ArtifactRoot))
        {
            try
            {
                Directory.Delete(ArtifactRoot, recursive: true);
            }
            catch (IOException)
            {
                // Melhor esforço na limpeza dos artefatos temporários.
            }
        }

        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        await ExecuteAsync(_masterConnectionString,
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
    }

    /// <summary>Cria um escopo de tenant/projeto único (isolado pela RLS e pelo filtro de projeto).</summary>
    public static TenantScope NewScope() =>
        new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    /// <summary>Store com aging desprezível (ordenação por prioridade pura), para testes gerais.</summary>
    public SqlJobStore Store(IClock clock) => new(Factory, clock, NoAging);

    /// <summary>Store com intervalo de aging explícito (para o teste anti-starvation).</summary>
    public SqlJobStore Store(IClock clock, TimeSpan agingInterval) => new(Factory, clock, agingInterval);

    /// <summary>LeaseManager com política e duração de lease informadas.</summary>
    public SqlJobLeaseManager LeaseManager(IClock clock, RetryPolicy policy, TimeSpan leaseDuration) =>
        new(Factory, clock, policy, leaseDuration);

    /// <summary>AuditReader (identidade da aplicação).</summary>
    public SqlJobAuditReader AuditReader() => new(Factory);

    private static string RandomPassword() => "Ab" + Guid.NewGuid().ToString("N") + "!9";

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
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
