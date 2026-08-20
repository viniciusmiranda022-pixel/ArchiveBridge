using System.Globalization;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MigrationHashTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Migration0017AppliesCleanlyAndPriorHashesRemainStable()
    {
        // A fixture já aplicou TODAS as migrations (incluindo a 0017). Re-executar o runner é idempotente E
        // revalida os hashes armazenados contra o conteúdo embutido: se qualquer migration 0011–0016 tivesse
        // divergido, isto lançaria. Um re-apply limpo prova que os hashes anteriores permanecem estáveis.
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 17;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var indexes = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_evd_scope_completed', 'IX_portal_sign_in_events_tenant_time_event');",
            connection);
        Assert.Equal(2, Convert.ToInt32(await indexes.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0018AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0017 tivesse divergido, isto lançaria. Um re-apply limpo prova que os hashes anteriores
        // permanecem estáveis; em seguida confirmamos a 0018 e as duas tabelas de custódia de validação.
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 18;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var tables = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name IN ('mapping_validation_attempts', 'mapping_validation_issues');",
            connection);
        Assert.Equal(2, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0019AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0018 tivesse divergido, isto lançaria. Um re-apply limpo prova que os hashes anteriores
        // permanecem estáveis; em seguida confirmamos a 0019 e a coluna/índice de idempotência do retry.
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 19;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var column = new SqlCommand(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.jobs') AND name = 'retry_idempotency_key';",
            connection))
        {
            Assert.Equal(1, Convert.ToInt32(await column.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var index = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_jobs_retry_idempotency';", connection);
        Assert.Equal(1, Convert.ToInt32(await index.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AnAppliedMigrationWithDivergentContentIsBlocked()
    {
        var original = await ReadHashAsync(1);
        await WriteHashAsync(1, new string('0', 64));
        try
        {
            var runner = new MigrationRunner(fixture.AdminConnectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(CancellationToken.None));
        }
        finally
        {
            await WriteHashAsync(1, original);
        }
    }

    private async Task<string> ReadHashAsync(int version)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT content_sha256 FROM dbo.schema_migrations WHERE version = @v;", connection);
        command.Parameters.Add(new SqlParameter("@v", System.Data.SqlDbType.Int) { Value = version });
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task WriteHashAsync(int version, string hash)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE dbo.schema_migrations SET content_sha256 = @h WHERE version = @v;", connection);
        command.Parameters.Add(new SqlParameter("@h", System.Data.SqlDbType.Char, 64) { Value = hash });
        command.Parameters.Add(new SqlParameter("@v", System.Data.SqlDbType.Int) { Value = version });
        await command.ExecuteNonQueryAsync();
    }
}
