using System.Globalization;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MaintenanceCredentialTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<int> CountAllJobsWithMaintenanceAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setContext = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'is_maintenance', @value = 1;", connection))
        {
            await setContext.ExecuteNonQueryAsync();
        }

        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.jobs;", connection);
        return Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ApplicationCredentialCannotEnableMaintenanceBypass()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var scope = SqlServerFixture.NewScope();
        await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        // Identidade da APLICAÇÃO define is_maintenance=1, mas não é membro de ab_maintenance_role:
        // a RLS não concede bypass — nenhuma linha cross-tenant é visível.
        var seenByApp = await CountAllJobsWithMaintenanceAsync(fixture.ConnectionString);
        Assert.Equal(0, seenByApp);

        // Identidade de MANUTENÇÃO (ab_reaper) é membro do papel: o bypass funciona.
        var seenByMaintenance = await CountAllJobsWithMaintenanceAsync(fixture.MaintenanceConnectionString);
        Assert.True(seenByMaintenance >= 1);
    }
}
