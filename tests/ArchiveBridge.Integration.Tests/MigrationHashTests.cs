using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MigrationHashTests(SqlServerFixture fixture)
{
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
