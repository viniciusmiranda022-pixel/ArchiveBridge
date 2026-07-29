using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Persistence;

/// <summary>
/// Aplica migrations SQL versionadas e embutidas no assembly. Propriedades:
/// ordenadas por versão; idempotentes quanto à execução (rastreadas em <c>schema_migrations</c>);
/// protegidas por hash (uma migration aplicada com conteúdo divergente bloqueia); serializadas por
/// <c>sp_getapplock</c>; sem alteração destrutiva automática (apenas cria objetos). Cada migration é
/// aplicada em uma transação junto do registro em <c>schema_migrations</c>.
/// </summary>
public sealed partial class MigrationRunner(string connectionString)
{
    private const string AppLockResource = "ArchiveBridge.Migrations";
    private readonly string _connectionString = connectionString;

    [GeneratedRegex(@"\.Migrations\.(\d+)_([^.]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationNamePattern();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BatchSeparatorPattern();

    /// <summary>Aplica as migrations pendentes e retorna as versões efetivamente aplicadas nesta execução.</summary>
    public async Task<IReadOnlyList<int>> ApplyAsync(CancellationToken cancellationToken)
    {
        var migrations = LoadMigrations();
        var applied = new List<int>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AcquireLockAsync(connection, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureHistoryTableAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var migration in migrations)
            {
                if (await TryApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(migration.Version);
                }
            }
        }
        finally
        {
            await ReleaseLockAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }

    private static List<Migration> LoadMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = MigrationNamePattern().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            var version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var name = match.Groups[2].Value;
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Recurso de migration ausente: {resource}");
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            var sql = streamReader.ReadToEnd();
            migrations.Add(new Migration(version, name, sql, ComputeHash(sql)));
        }

        migrations.Sort(static (left, right) => left.Version.CompareTo(right.Version));
        return migrations;
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static async Task AcquireLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "DECLARE @result INT; " +
            "EXEC @result = sys.sp_getapplock @Resource = @res, @LockMode = 'Exclusive', " +
            "@LockOwner = 'Session', @LockTimeout = 30000; " +
            "SELECT @result;",
            connection);
        command.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = AppLockResource });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var code = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        if (code < 0)
        {
            throw new InvalidOperationException($"Não foi possível obter o applock de migrations (código {code}).");
        }
    }

    private static async Task ReleaseLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "IF APPLOCK_MODE('public', @res, 'Session') <> 'NoLock' " +
            "EXEC sys.sp_releaseapplock @Resource = @res, @LockOwner = 'Session';",
            connection);
        command.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = AppLockResource });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureHistoryTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.schema_migrations', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.schema_migrations
                (
                    version        INT           NOT NULL CONSTRAINT PK_schema_migrations PRIMARY KEY,
                    name           NVARCHAR(200) NOT NULL,
                    content_sha256 CHAR(64)      NOT NULL,
                    applied_at_utc DATETIME2(3)  NOT NULL
                        CONSTRAINT DF_schema_migrations_applied DEFAULT SYSUTCDATETIME()
                );
            END
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryApplyAsync(SqlConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        var appliedHash = await ReadAppliedHashAsync(connection, migration.Version, cancellationToken).ConfigureAwait(false);
        if (appliedHash is not null)
        {
            if (!string.Equals(appliedHash, migration.Hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration {migration.Version} ({migration.Name}) diverge do conteúdo já aplicado " +
                    "(hash diferente). Bloqueado para evitar drift; não há alteração destrutiva automática.");
            }

            return false;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            foreach (var batch in SplitBatches(migration.Sql))
            {
                await ExecuteBatchAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
            }

            await using (var record = new SqlCommand(
                "INSERT INTO dbo.schema_migrations (version, name, content_sha256) VALUES (@v, @n, @h);",
                connection,
                transaction))
            {
                record.Parameters.Add(new SqlParameter("@v", SqlDbType.Int) { Value = migration.Version });
                record.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 200) { Value = migration.Name });
                record.Parameters.Add(new SqlParameter("@h", SqlDbType.Char, 64) { Value = migration.Hash });
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> ReadAppliedHashAsync(
        SqlConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT content_sha256 FROM dbo.schema_migrations WHERE version = @v;",
            connection);
        command.Parameters.Add(new SqlParameter("@v", SqlDbType.Int) { Value = version });
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    private static async Task ExecuteBatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string batch,
        CancellationToken cancellationToken)
    {
        // O texto vem de migrations embutidas no assembly (conteúdo de compilação confiável, não
        // entrada de usuário); DDL não é parametrizável. Suppressão restrita ao ponto de execução.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
        await using var command = new SqlCommand(batch, connection, transaction);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        foreach (var batch in BatchSeparatorPattern().Split(sql))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                yield return batch;
            }
        }
    }

    private sealed record Migration(int Version, string Name, string Sql, string Hash);
}
