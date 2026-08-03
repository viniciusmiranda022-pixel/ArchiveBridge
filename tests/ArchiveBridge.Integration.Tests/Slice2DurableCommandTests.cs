using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B4: fluxo REAL de Jobs duráveis. O enfileiramento é atômico (Job + contexto versionado); o
/// processador reivindica (claim exclusivo, fencing), executa e conclui — ou falha terminalmente em
/// erro de domínio. Sem Job Pending decorativo. Reexecução idempotente não duplica efeito.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2DurableCommandTests(SqlServerFixture fixture)
{
    private static readonly WorkerId Worker = new("planning-worker");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private async Task<MigrationWave> SeedWaveAsync(long size)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", size)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return wave;
    }

    private async Task<int> ScalarAsync(TenantScope scope, string sql, Guid parameter)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, connection.Connection);
        command.Parameters.AddWithValue("@id", parameter);
        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task EnqueueCreatesJobAndContextAtomically()
    {
        var wave = await SeedWaveAsync(10);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var clock = new MutableClock(Slice2Support.Now);
        var command = PlanningCommandFactory.ValidateWave(wave, CorrelationId.New());

        var jobId = await Slice2Support.Inbox(fixture, clock).EnqueueAsync(command, CancellationToken.None);

        // Job de controle Pending existe.
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.jobs WHERE job_id = @id AND workload = 0 AND state = 0;", jobId.Value));
        // Contexto do comando existe (mesma transação) com versão de seleção fixada.
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.planning_commands WHERE job_id = @id AND command_type = 1 AND expected_wave_version = 1;", jobId.Value));
    }

    [Fact]
    public async Task ClaimExecuteCompleteRunsTheOperation()
    {
        var wave = await SeedWaveAsync(10);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var clock = new MutableClock(Slice2Support.Now);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()), CancellationToken.None);

        var execution = await Slice2Support.Processor(fixture, clock)
            .ProcessNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(PlanningCommandKind.ValidateWave, execution!.Kind);
        Assert.Equal(PlanningCommandOutcome.Completed, execution.Outcome);

        var reloaded = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(WaveStatus.ReadyForApproval, reloaded!.Status);
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.jobs WHERE job_id = @id AND state = 3;", execution.Job.Value));
    }

    [Fact]
    public async Task FailingCommandFailsJobTerminally()
    {
        // GenerateMappingCsv de uma onda NÃO aprovada ⇒ erro de domínio ⇒ Job falhado (terminal).
        var wave = await SeedWaveAsync(10);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var clock = new MutableClock(Slice2Support.Now);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            PlanningCommandFactory.GenerateMappingCsv(wave, new ContentCodePage(1252), "do", MappingPolicy.Default, CorrelationId.New()),
            CancellationToken.None);

        var execution = await Slice2Support.Processor(fixture, clock)
            .ProcessNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(PlanningCommandOutcome.Failed, execution!.Outcome);
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.jobs WHERE job_id = @id AND state = 4;", execution.Job.Value));
    }

    [Fact]
    public async Task NoWorkReturnsNull()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var clock = new MutableClock(Slice2Support.Now);

        var execution = await Slice2Support.Processor(fixture, clock)
            .ProcessNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.Null(execution);
    }

    [Fact]
    public async Task IdempotentReexecutionDoesNotDuplicateAssessments()
    {
        var wave = await SeedWaveAsync(10);
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var clock = new MutableClock(Slice2Support.Now);
        var useCase = Slice2Support.ValidateWave(fixture, clock);

        await useCase.ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        await useCase.ExecuteAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        // Uma entrada/archive ⇒ exatamente uma avaliação registrada, não duas.
        Assert.Equal(1, await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.planning_assessments WHERE wave_id = @id;", wave.Id.Value));
    }
}
