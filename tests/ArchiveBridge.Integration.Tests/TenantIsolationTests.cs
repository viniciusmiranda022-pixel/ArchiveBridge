using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class TenantIsolationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task TenantCannotReadAnotherTenantsJob()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var tenantA = SqlServerFixture.NewScope();
        var tenantB = SqlServerFixture.NewScope();

        var jobId = await store.CreateAsync(
            new CreateJobCommand(tenantA, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        Assert.NotNull(await store.GetAsync(tenantA, jobId, CancellationToken.None));
        Assert.Null(await store.GetAsync(tenantB, jobId, CancellationToken.None));
    }

    [Fact]
    public async Task TenantCannotClaimAnotherTenantsJobs()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var tenantA = SqlServerFixture.NewScope();
        var tenantB = SqlServerFixture.NewScope();

        await store.CreateAsync(
            new CreateJobCommand(tenantA, Workload.Control, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        var claimedByB = await store.TryClaimNextAsync(
            new ClaimRequest(tenantB, Workload.Control, new WorkerId("b"), Lease, CorrelationId.New()),
            CancellationToken.None);

        Assert.Null(claimedByB);
    }

    [Fact]
    public async Task TenantCannotCompleteAnotherTenantsJob()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var tenantA = SqlServerFixture.NewScope();
        var tenantB = new TenantScope(new TenantId(Guid.NewGuid()), tenantA.Project);

        var jobId = await store.CreateAsync(
            new CreateJobCommand(tenantA, Workload.Upload, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(tenantA, Workload.Upload, new WorkerId("a"), Lease, CorrelationId.New()),
            CancellationToken.None);

        // Tenant B tenta concluir o Job de A (mesmo job_id/worker/época): a RLS esconde a linha.
        var outcome = await store.CompleteAsync(
            new LeaseCommand(tenantB, jobId, new WorkerId("a"), claimed!.Epoch, CorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(JobCommandOutcome.NotFound, outcome);

        // O Job de A permanece em Processing (intacto).
        var job = await store.GetAsync(tenantA, jobId, CancellationToken.None);
        Assert.Equal(JobState.Processing, job!.State);
    }

    [Fact]
    public async Task ConnectionWithoutTenantContextSeesNoRows()
    {
        var clock = new MutableClock(Start);
        var store = fixture.Store(clock);
        var scope = SqlServerFixture.NewScope();
        await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        // Conexão crua, sem definir SESSION_CONTEXT('tenant_id'): a RLS bloqueia tudo (fail-closed).
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.jobs;", connection);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task PooledConnectionDoesNotInheritThePreviousTenant()
    {
        var scope = SqlServerFixture.NewScope();
        // Pool com uma única conexão física: a segunda abertura reusa a mesma conexão.
        var singlePool = new SqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        var factory = new ArchiveBridge.Infrastructure.Persistence.TenantConnectionFactory(singlePool);

        await using (var tenantConnection = await factory.OpenForTenantAsync(scope, CancellationToken.None))
        {
            await using var check = new SqlCommand(
                "SELECT CAST(SESSION_CONTEXT(N'tenant_id') AS UNIQUEIDENTIFIER);", tenantConnection.Connection);
            var current = await check.ExecuteScalarAsync();
            Assert.Equal(scope.Tenant.Value, Assert.IsType<Guid>(current));
        }

        // Reusa a conexão do pool sem definir contexto: não pode herdar o tenant anterior.
        await using var reused = new SqlConnection(singlePool);
        await reused.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT CAST(SESSION_CONTEXT(N'tenant_id') AS UNIQUEIDENTIFIER);", reused);
        var leaked = await command.ExecuteScalarAsync();

        Assert.True(leaked is null or DBNull);
    }
}
