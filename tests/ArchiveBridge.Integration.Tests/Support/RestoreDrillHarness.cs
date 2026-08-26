using System.Diagnostics;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>
/// Restore-drill harness seguro (AB-I7-005 item 3): provisiona SEU PRÓPRIO banco de teste efêmero
/// dedicado no SQL Server real (nunca o banco compartilhado de <see cref="SqlServerFixture"/>, para não
/// interferir com os demais testes de integração que rodam na mesma coleção), aplica as migrations reais,
/// e executa BACKUP/RESTORE nativo do próprio SQL Server sobre ESSE banco — nunca sobre
/// produção/cliente (STOP-THE-LINE do work order). Cada operação mede a duração REAL observada
/// (<see cref="Stopwatch"/>), nunca um valor alegado, para servir de evidência de RTO.
/// <para>
/// O nome do banco é sempre gerado por este harness (prefixo próprio, nunca informado externamente) —
/// estruturalmente impossível apontar para um banco arbitrário/de produção.
/// </para>
/// </summary>
public sealed class RestoreDrillHarness : IAsyncDisposable
{
    private const string EnvironmentVariable = "ARCHIVEBRIDGE_TEST_SQL";
    private const string DatabasePrefix = "ab_i7_dr_drill_";

    private readonly string _databaseName;
    private readonly string _masterConnectionString;
    private readonly string _backupFilePath;

    private RestoreDrillHarness(
        string databaseName, string masterConnectionString, string adminConnectionString,
        TenantConnectionFactory factory, string backupFilePath)
    {
        _databaseName = databaseName;
        _masterConnectionString = masterConnectionString;
        AdminConnectionString = adminConnectionString;
        Factory = factory;
        _backupFilePath = backupFilePath;
    }

    /// <summary>Conexão administrativa (sa) do banco efêmero do drill — usada por migrations/DDL/inspeção direta.</summary>
    public string AdminConnectionString { get; }

    /// <summary>Fábrica de conexões (app + manutenção) ligada ao banco efêmero do drill.</summary>
    public TenantConnectionFactory Factory { get; }

    /// <summary>Provisiona o banco efêmero dedicado do drill, aplica as migrations reais e cria as identidades contidas.</summary>
    public static async Task<RestoreDrillHarness> CreateAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"Defina {EnvironmentVariable} com a conexão sa do SQL Server de teste — o restore drill exige SQL Server real.");

        var databaseName = DatabasePrefix + Guid.NewGuid().ToString("N");
        var masterConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }.ConnectionString;

        await ExecuteAsync(masterConnectionString, "EXEC sys.sp_configure N'contained database authentication', 1; RECONFIGURE;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(masterConnectionString, $"ALTER DATABASE [{databaseName}] SET CONTAINMENT = PARTIAL;", cancellationToken).ConfigureAwait(false);

        var adminConnectionString = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = databaseName }.ConnectionString;
        await new MigrationRunner(adminConnectionString).ApplyAsync(cancellationToken).ConfigureAwait(false);

        var appPassword = RandomPassword();
        var reaperPassword = RandomPassword();
        await ExecuteAsync(adminConnectionString,
            $"CREATE USER ab_app WITH PASSWORD = '{appPassword}'; ALTER ROLE ab_app_role ADD MEMBER ab_app;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(adminConnectionString,
            $"CREATE USER ab_reaper WITH PASSWORD = '{reaperPassword}'; ALTER ROLE ab_maintenance_role ADD MEMBER ab_reaper;", cancellationToken)
            .ConfigureAwait(false);

        var appConnectionString = new SqlConnectionStringBuilder(baseConnectionString)
        { InitialCatalog = databaseName, UserID = "ab_app", Password = appPassword }.ConnectionString;
        var maintenanceConnectionString = new SqlConnectionStringBuilder(baseConnectionString)
        { InitialCatalog = databaseName, UserID = "ab_reaper", Password = reaperPassword }.ConnectionString;
        var factory = new TenantConnectionFactory(appConnectionString, maintenanceConnectionString);

        Directory.CreateDirectory(backupDirectory);
        var backupFilePath = Path.Combine(backupDirectory, databaseName + ".bak");

        return new RestoreDrillHarness(databaseName, masterConnectionString, adminConnectionString, factory, backupFilePath);
    }

    /// <summary>Executa um BACKUP DATABASE real sobre o banco efêmero do drill. Retorna a duração REAL observada.</summary>
    public async Task<TimeSpan> BackupAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await ExecuteAsync(_masterConnectionString,
            $"BACKUP DATABASE [{_databaseName}] TO DISK = N'{_backupFilePath}' WITH INIT;", cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Executa um RESTORE DATABASE real (WITH REPLACE) sobre o MESMO banco efêmero do drill — nunca produção
    /// (o nome do banco é sempre o gerado por <see cref="CreateAsync"/>). Retorna a duração REAL observada.
    /// </summary>
    public async Task<TimeSpan> RestoreAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await ExecuteAsync(_masterConnectionString, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await ExecuteAsync(_masterConnectionString, $"RESTORE DATABASE [{_databaseName}] FROM DISK = N'{_backupFilePath}' WITH REPLACE;", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await ExecuteAsync(_masterConnectionString, $"ALTER DATABASE [{_databaseName}] SET MULTI_USER;", cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ExecuteAsync(_masterConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // Melhor esforço na limpeza do banco temporário do drill.
        }

        try
        {
            if (File.Exists(_backupFilePath))
            {
                File.Delete(_backupFilePath);
            }
        }
        catch (IOException)
        {
            // Melhor esforço na limpeza do arquivo de backup temporário.
        }
    }

    private static string RandomPassword() => "Ab" + Guid.NewGuid().ToString("N") + "!9";

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
