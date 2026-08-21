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
        // permanecem estáveis; em seguida confirmamos a 0019 e o livro-razão de idempotência do retry
        // (ajustado em AB-7-002: dbo.job_retry_requests, não mais uma coluna em dbo.jobs).
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 19;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var table = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'job_retry_requests';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await table.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var index = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_job_retry_requests_job';", connection);
        Assert.Equal(1, Convert.ToInt32(await index.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0021AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0020 tivesse divergido (inclusive as do Passo 1 do Slice 4B), isto lançaria. Em seguida
        // confirmamos a 0021 e os objetos aditivos do planejamento de particionamento.
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 21;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var tables = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name IN ('pst_partition_plans', 'pst_partition_plan_parts');",
            connection))
        {
            Assert.Equal(2, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // O índice único FILTRADO de canonicidade é o backstop de idempotência sob concorrência.
        await using (var canonicalIndex = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_pst_partition_plans_canonical' AND has_filter = 1;",
            connection))
        {
            Assert.Equal(1, Convert.ToInt32(await canonicalIndex.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Append-only: a aplicação recebe apenas SELECT/INSERT nas tabelas novas (nenhum UPDATE/DELETE).
        await using var grants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role'
              AND o.name IN ('pst_partition_plans', 'pst_partition_plan_parts')
              AND p.permission_name NOT IN ('SELECT', 'INSERT');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await grants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0022AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0021 tivesse divergido (inclusive a do Passo 2 do Slice 4B), isto lançaria. Em seguida
        // confirmamos a 0022 e os objetos aditivos da execução de particionamento.
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 22;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var tables = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'pst_partition_executions';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Toda linha é canônica por construção: o índice único (não filtrado) já é o backstop completo de
        // idempotência/concorrência.
        await using (var canonicalIndex = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_pst_partition_executions_canonical' AND has_filter = 0 AND is_unique = 1;",
            connection))
        {
            Assert.Equal(1, Convert.ToInt32(await canonicalIndex.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Append-only: a aplicação recebe apenas SELECT/INSERT na tabela nova (nenhum UPDATE/DELETE).
        await using var grants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role'
              AND o.name = 'pst_partition_executions'
              AND p.permission_name NOT IN ('SELECT', 'INSERT');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await grants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0023AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0022 tivesse divergido (inclusive a do Passo 3 do Slice 4B), isto lançaria. Em seguida
        // confirmamos a 0023 e as cinco tabelas da fundação de connector EV (Slice 4C, Passo 1).
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 23;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var tables = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.tables WHERE name IN (
                'ev_connector_enrollment_tokens', 'ev_connectors', 'ev_connector_capability_handshakes',
                'ev_connector_inventory_snapshots', 'ev_connector_inventory_archives');
            """,
            connection))
        {
            Assert.Equal(5, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // export_capable é sempre reforçado no BANCO como derivado de support_level + snap-in — defesa em
        // profundidade da MESMA regra do Domain (ConnectorCapabilityHandshake.Evaluate).
        await using (var exportCheck = new SqlCommand(
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_ev_cch_export_capable';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await exportCheck.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Append-only: a aplicação recebe apenas SELECT/INSERT nas tabelas de evidência histórica (handshakes
        // e inventário); enrollment tokens e connectors permitem UPDATE apenas dos campos mutáveis previstos.
        await using var grants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role'
              AND o.name IN ('ev_connector_capability_handshakes', 'ev_connector_inventory_snapshots', 'ev_connector_inventory_archives')
              AND p.permission_name NOT IN ('SELECT', 'INSERT');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await grants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0024AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0023 tivesse divergido (inclusive as do Passo 1 do Slice 4C), isto lançaria. Em seguida
        // confirmamos a 0024 e as seis tabelas da fundação de EXECUÇÃO de export EV (Slice 4C, Passo 2).
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 24;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var tables = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.tables WHERE name IN (
                'ev_export_requests', 'ev_export_throttle_leases', 'ev_export_attempts',
                'ev_export_manifest_entries', 'ev_export_oversized_items', 'ev_export_events');
            """,
            connection))
        {
            Assert.Equal(6, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Backstop atômico de throttling (item 4): os DOIS índices únicos FILTRADOS sobre a mesma tabela.
        await using (var throttleIndexes = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID('dbo.ev_export_throttle_leases')
              AND name IN ('UX_ev_export_throttle_leases_connector', 'UX_ev_export_throttle_leases_archive')
              AND is_unique = 1 AND has_filter = 1;
            """,
            connection))
        {
            Assert.Equal(2, Convert.ToInt32(await throttleIndexes.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Manifesto/engine só existem quando a tentativa foi Completed — reforçado no BANCO (defesa em
        // profundidade da mesma regra do Domain).
        await using (var manifestCheck = new SqlCommand(
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_ev_export_attempts_manifest_only_when_completed';",
            connection))
        {
            Assert.Equal(1, Convert.ToInt32(await manifestCheck.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Append-only: a aplicação recebe apenas SELECT/INSERT nas tabelas de evidência histórica (attempts,
        // manifesto, oversized, eventos, requests); throttle_leases é a ÚNICA exceção (permite UPDATE
        // restrito a released_at_utc, a liberação do lease).
        await using var grants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role'
              AND o.name IN ('ev_export_requests', 'ev_export_attempts', 'ev_export_manifest_entries',
                              'ev_export_oversized_items', 'ev_export_events')
              AND p.permission_name NOT IN ('SELECT', 'INSERT');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await grants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
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
