using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// B1: o comando durável é VINCULADO à versão que o originou. No processamento, o worker compara o
/// contexto fixado com o estado corrente; divergência (versão/hash de seleção, hash de configuração,
/// schema desconhecido) ⇒ STALE_COMMAND_CONTEXT (falha terminal), nunca execução silenciosa sobre a
/// versão posterior. Um comando da versão CORRENTE continua válido (retry).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2CommandContextTests(SqlServerFixture fixture)
{
    private static readonly WorkerId Worker = new("planning-worker");
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private async Task<MigrationWave> SeedWaveAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        return wave;
    }

    private async Task<PlanningCommandOutcome> EnqueueAndProcessAsync(MigrationWave wave, PlanningCommand command)
    {
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var clock = new MutableClock(Slice2Support.Now);
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(command, CancellationToken.None);
        var execution = await Slice2Support.Processor(fixture, clock)
            .ProcessNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(execution);
        return execution!.Outcome;
    }

    private async Task<int> JobStateAsync(TenantScope scope, Guid jobId)
    {
        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand("SELECT state FROM dbo.jobs WHERE job_id = @id;", connection.Connection);
        command.Parameters.AddWithValue("@id", jobId);
        return (byte)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    [Fact]
    public async Task MatchingVersionCommandSucceeds()
    {
        var wave = await SeedWaveAsync();
        var outcome = await EnqueueAndProcessAsync(wave, PlanningCommandFactory.ValidateWave(wave, CorrelationId.New()));
        Assert.Equal(PlanningCommandOutcome.Completed, outcome);
    }

    [Fact]
    public async Task StaleSelectionVersionFailsCommand()
    {
        var wave = await SeedWaveAsync();
        var scope = new TenantScope(wave.Tenant, wave.Project);
        var command = PlanningCommandFactory.ValidateWave(wave, CorrelationId.New());

        // Enfileira o comando da v1 e, depois, muda a seleção (⇒ v2). O comando torna-se obsoleto.
        var clock = new MutableClock(Slice2Support.Now);
        var jobId = await Slice2Support.Inbox(fixture, clock).EnqueueAsync(command, CancellationToken.None);

        var reloaded = await Slice2Support.WaveStore(fixture).GetAsync(scope, wave.Id, CancellationToken.None);
        reloaded!.UpdateSelection(new WaveSelection([Slice2Support.Entry("b.pst", "w@contoso.com", 20)]));
        await Slice2Support.WaveStore(fixture).SaveSelectionAsync(reloaded, CorrelationId.New(), CancellationToken.None);

        var execution = await Slice2Support.Processor(fixture, clock)
            .ProcessNextAsync(scope, Worker, Lease, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PlanningCommandOutcome.Failed, execution!.Outcome);
        Assert.Equal(4, await JobStateAsync(scope, jobId.Value)); // Failed (terminal)
    }

    [Fact]
    public async Task DivergentSelectionHashFailsCommand()
    {
        var wave = await SeedWaveAsync();
        var command = PlanningCommandFactory.ValidateWave(wave, CorrelationId.New());
        var tampered = command with { Context = command.Context with { ExpectedSelectionHash = new Sha256Hash("deadbeef") } };
        Assert.Equal(PlanningCommandOutcome.Failed, await EnqueueAndProcessAsync(wave, tampered));
    }

    [Fact]
    public async Task DivergentConfigurationHashFailsCommand()
    {
        var wave = await SeedWaveAsync();
        var command = PlanningCommandFactory.ValidateWave(wave, CorrelationId.New());
        var tampered = command with { Context = command.Context with { ExpectedConfigurationHash = new Sha256Hash("deadbeef") } };
        Assert.Equal(PlanningCommandOutcome.Failed, await EnqueueAndProcessAsync(wave, tampered));
    }

    [Fact]
    public async Task UnknownSchemaFailsCommand()
    {
        var wave = await SeedWaveAsync();
        var command = PlanningCommandFactory.ValidateWave(wave, CorrelationId.New());
        var tampered = command with { Context = command.Context with { SchemaVersion = 999 } };
        Assert.Equal(PlanningCommandOutcome.Failed, await EnqueueAndProcessAsync(wave, tampered));
    }
}
