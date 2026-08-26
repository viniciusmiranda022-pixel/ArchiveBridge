using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-001 item 2/#11 (chaos case "identidade/permissão é removida durante a operação") — prova, contra
/// SQL Server real (os papéis contidos <c>ab_app_role</c>/<c>ab_maintenance_role</c> são o mecanismo REAL de
/// autorização, ver <c>0002_security_roles.sql</c>), que perder a permissão da identidade da aplicação NO
/// MEIO de uma operação nunca é tratado como sucesso nem deixa estado parcial — a exceção de SQL propaga
/// fail-closed — e que a negação NUNCA é permanente: restaurada a permissão, a MESMA identidade volta a
/// operar normalmente, sem qualquer estado corrompido/irrecuperável.
/// <para>
/// Executa por ÚLTIMO dentro da coleção compartilhada (mutação real de papel/membro no banco de teste
/// inteiro) — a coleção <see cref="SqlServerCollectionDefinition"/> já serializa TODOS os testes de
/// integração (nenhum outro teste roda concorrentemente enquanto a permissão está revogada), e o
/// <c>finally</c> restaura a associação incondicionalmente, mesmo se a asserção falhar.
/// </para>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class IdentityPermissionRevocationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RevokingTheApplicationRoleMidOperationFailsClosedWithoutPartialStateAndRecoversAfterRestoration()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var store = fixture.Store(clock);

        await ExecuteAdminAsync("ALTER ROLE ab_app_role DROP MEMBER ab_app;");
        try
        {
            // A identidade que a aplicação usa (ab_app) perdeu a permissão de gravar em dbo.jobs — a
            // aplicação NUNCA deve interpretar isso como sucesso nem persistir efeito parcial: a exceção do
            // SQL Server propaga tal como veio (fail-closed), sem ser engolida/traduzida em um resultado de
            // negócio silencioso.
            await Assert.ThrowsAnyAsync<SqlException>(() => store.CreateAsync(
                new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()), CancellationToken.None));
        }
        finally
        {
            // Restaura INCONDICIONALMENTE, mesmo se a asserção acima falhar — nunca deixa a coleção inteira
            // de testes de integração num estado permanentemente quebrado.
            await ExecuteAdminAsync("ALTER ROLE ab_app_role ADD MEMBER ab_app;");
        }

        // Nenhum job parcial foi criado durante a janela sem permissão (a transação da tentativa negada
        // nunca commitou).
        Assert.Equal(0, await CountJobsAsync(scope));

        // Depois de restaurada a permissão, a MESMA identidade volta a operar normalmente — a negação
        // temporária nunca deixou nada permanentemente corrompido/irrecuperável.
        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);
        Assert.NotEqual(Guid.Empty, jobId.Value);
        Assert.Equal(1, await CountJobsAsync(scope));
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountJobsAsync(TenantScope scope)
    {
        // dbo.jobs tem Row-Level Security por tenant (0003_row_level_security.sql): mesmo a conexão
        // administrativa (sa) só enxerga linhas cujo tenant_id bate com SESSION_CONTEXT('tenant_id') (ou
        // modo de manutenção autorizado) — nunca todas as linhas incondicionalmente. Sem isto, a contagem
        // sempre veria 0, mascarando o efeito real gravado (mesmo padrão já usado por
        // Slice4bPartitionExecutionTests para leitura administrativa de dbo.*).
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.AddWithValue("@tenant", scope.Tenant.Value);
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.jobs WHERE tenant_id = @tenant AND project_id = @project", connection);
        command.Parameters.AddWithValue("@tenant", scope.Tenant.Value);
        command.Parameters.AddWithValue("@project", scope.Project.Value);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
