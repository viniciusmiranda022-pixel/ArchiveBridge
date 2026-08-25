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
        // confirmamos a 0024 e as SETE tabelas da fundação de EXECUÇÃO de export EV (Slice 4C, Passo 2;
        // o throttling recuperável de AB-4C-007 substituiu o ledger único original por DOIS slots).
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
                'ev_export_requests', 'ev_export_connector_throttle_slots', 'ev_export_archive_throttle_slots',
                'ev_export_attempts', 'ev_export_manifest_entries', 'ev_export_oversized_items', 'ev_export_events');
            """,
            connection))
        {
            Assert.Equal(7, Convert.ToInt32(await tables.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // Backstop de throttling recuperável (item 4; AB-4C-007 blocker 1): cada slot é uma PK própria
        // (connector_id / tenant+projeto+archive) — a exclusividade em si vem da PK, não de um índice
        // filtrado (o slot é reutilizado/reclamado, nunca reinserido).
        await using (var slotPrimaryKeys = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.key_constraints
            WHERE name IN ('PK_ev_export_connector_throttle_slots', 'PK_ev_export_archive_throttle_slots');
            """,
            connection))
        {
            Assert.Equal(2, Convert.ToInt32(await slotPrimaryKeys.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        // A consistência dos DOIS slots (todos os campos de lease nulos juntos, ou todos preenchidos) é
        // reforçada no BANCO como defesa em profundidade.
        await using (var slotChecks = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE name IN ('CK_ev_export_connector_throttle_slots_consistency', 'CK_ev_export_archive_throttle_slots_consistency');
            """,
            connection))
        {
            Assert.Equal(2, Convert.ToInt32(await slotChecks.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
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
        // manifesto, oversized, eventos, requests); os DOIS slots de throttle são a exceção (permitem UPDATE
        // restrito aos campos mutáveis do lease — nunca DELETE).
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

        await using var throttleSlotGrants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role'
              AND o.name IN ('ev_export_connector_throttle_slots', 'ev_export_archive_throttle_slots')
              AND p.permission_name NOT IN ('SELECT', 'INSERT', 'UPDATE');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await throttleSlotGrants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0030AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0029 tivesse divergido, isto lançaria. Confirma a 0030 e a nova coluna entry_id (AB-I5-013).
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 30;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var column = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.wave_partition_output_bindings') AND name = 'entry_id';
            """,
            connection);
        Assert.Equal(1, Convert.ToInt32(await column.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0031AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0030 tivesse divergido, isto lançaria. Confirma a 0031 (AB-I5-012), o índice único de
        // utilizável, e que a role da aplicação só pode UPDATE a coluna status.
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 31;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var table = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'purview_mapping_csv_versions';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await table.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var index = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_pmcv_single_usable';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await index.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var grants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role' AND o.name = 'purview_mapping_csv_versions'
              AND p.permission_name NOT IN ('SELECT', 'INSERT', 'UPDATE');
            """,
            connection);
        Assert.Equal(0, Convert.ToInt32(await grants.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migration0032AppliesCleanlyAndPriorHashesRemainStable()
    {
        // Re-executar o runner é idempotente E revalida os hashes armazenados: se qualquer migration
        // 0001–0031 tivesse divergido, isto lançaria. Confirma a 0032 (AB-I5-015): a coluna manifest_hash,
        // a tabela nova de manifestação por arquivo, seu índice único de execução e que a role da aplicação
        // recebe apenas SELECT/INSERT (append-only).
        var runner = new MigrationRunner(fixture.AdminConnectionString);
        await runner.ApplyAsync(CancellationToken.None); // não lança

        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();

        await using (var applied = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.schema_migrations WHERE version = 32;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await applied.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var column = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.purview_upload_attempts') AND name = 'manifest_hash';
            """,
            connection))
        {
            Assert.Equal(1, Convert.ToInt32(await column.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var table = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'purview_upload_attempt_manifest_items';", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await table.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using (var index = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_puami_execution' AND is_unique = 1;", connection))
        {
            Assert.Equal(1, Convert.ToInt32(await index.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }

        await using var grants = new SqlCommand(
            """
            SELECT COUNT(*) FROM sys.database_permissions AS p
            JOIN sys.objects AS o ON o.object_id = p.major_id
            JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
            WHERE r.name = 'ab_app_role' AND o.name = 'purview_upload_attempt_manifest_items'
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
