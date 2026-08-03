using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B2: o claim de comandos de planejamento é EXCLUSIVO — só reivindica Jobs de controle que possuam
/// contexto em <c>planning_commands</c> (EXISTS, atômico com o carregamento do contexto). Um Job de
/// controle sem contexto não é reivindicado (não fica preso em Processing); um Job de outro workload
/// não é capturado; o claim é isolado por projeto.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2ExclusiveClaimTests(SqlServerFixture fixture)
{
    private static readonly WorkerId Worker = new("planning-worker");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private async Task<int> JobStateAsync(TenantScope scope, Guid jobId)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand("SELECT state FROM dbo.jobs WHERE job_id = @id;", connection.Connection);
        command.Parameters.AddWithValue("@id", jobId);
        return (byte)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task ControlJobWithoutContextIsNotClaimedAndStaysPending()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Slice2Support.Now);
        var jobStore = fixture.Store(clock);
        var jobId = await jobStore.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, new JobPriority(0), CorrelationId.New()), CancellationToken.None);

        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.Null(claimed);
        Assert.Equal(0, await JobStateAsync(scope, jobId.Value)); // continua Pending, não Processing
    }

    [Fact]
    public async Task OtherWorkloadJobIsNotClaimed()
    {
        var scope = SqlServerFixture.NewScope();
        var clock = new MutableClock(Slice2Support.Now);
        var jobStore = fixture.Store(clock);
        await jobStore.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, new JobPriority(0), CorrelationId.New()), CancellationToken.None);

        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task PlanningClaimIsIsolatedByProject()
    {
        var scopeA = SqlServerFixture.NewScope();
        var wave = Slice2Support.NewWave(scopeA, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        var clock = new MutableClock(Slice2Support.Now);
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scopeA), CorrelationId.New(), CancellationToken.None);
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            ArchiveBridge.Application.Planning.PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()), CancellationToken.None);

        // Outro projeto (mesmo tenant) não enxerga o comando enfileirado em scopeA.
        var scopeB = new TenantScope(scopeA.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scopeB, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.Null(claimed);
    }
}
