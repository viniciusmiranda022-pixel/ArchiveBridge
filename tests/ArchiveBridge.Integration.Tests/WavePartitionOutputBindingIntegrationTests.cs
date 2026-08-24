using System.Data;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I5-010 (SQL Server real) — o vínculo canônico wave → output de particionamento: persistência,
/// canonicidade, convergência idempotente sob corrida real, isolamento cross-tenant/project (RLS) e
/// detecção de adulteração do <c>binding_hash</c> diretamente na linha.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class WavePartitionOutputBindingIntegrationTests(SqlServerFixture fixture)
{
    private static readonly IClock Clock = new SystemClock();

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private CreateWavePartitionOutputBindingUseCase UseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    /// <summary>Cria projeto + onda aprovada e registra/inspeciona/planeja/executa um PST real de teste.</summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, PartitionExecutionRecord Execution)> SeedWaveAndExecutionAsync(string name)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative), DeterministicHash.ComputeBytes(bytes), bytes.Length,
            CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var execution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture).ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        var wave = Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry(name, "user@contoso.com", bytes.Length)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        return (scope, wave, execution);
    }

    [Fact]
    public async Task ThePersistedBindingReidratesExactlyAsSavedAndIsFoundByFindCanonicalAndListForWave()
    {
        var (scope, wave, execution) = await SeedWaveAndExecutionAsync("binding-happy.pst");

        var created = await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        var reread = await Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal(created.Id, reread!.Id);
        Assert.Equal(execution.Id, reread.Execution);
        Assert.Equal(execution.OutputHash, reread.OutputHash);

        var forWave = await Bindings().ListForWaveAsync(scope, wave.Id, CancellationToken.None);
        Assert.Single(forWave);
        Assert.Equal(created.Id, forWave[0].Id);
    }

    [Fact]
    public async Task ARepeatedBindingRequestForTheSameWavePlanAndPartConvergesWithoutDuplicatingUnderRealSql()
    {
        var (scope, wave, execution) = await SeedWaveAndExecutionAsync("binding-idempotent.pst");
        var useCase = UseCase();

        var first = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.wave_partition_output_bindings WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TwoConcurrentBindingCreationsForTheSameWavePlanAndPartNeverProduceTwoCanonicalRows()
    {
        var (scope, wave, execution) = await SeedWaveAndExecutionAsync("binding-race.pst");
        var request = new CreateWavePartitionOutputBindingRequest(scope, wave.Id, execution.Plan, execution.Part, CorrelationId.New());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => UseCase().ExecuteAsync(request, CancellationToken.None)));

        var distinctIds = results.Select(binding => binding.Id).Distinct().Count();
        Assert.Equal(1, distinctIds); // todas as chamadas convergem para o MESMO vínculo canônico.

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.wave_partition_output_bindings WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ABindingFromAnotherProjectIsIndistinguishableFromNotFound()
    {
        var (scope, wave, execution) = await SeedWaveAndExecutionAsync("binding-cross-project.pst");
        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        var otherProjectScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        var fromOtherProject = await Bindings()
            .FindCanonicalAsync(otherProjectScope, wave.Id, execution.Plan, execution.Part, CancellationToken.None);

        Assert.Null(fromOtherProject);
    }

    [Fact]
    public async Task GetCanonicalFailsClosedWhenTheBindingHashIsTamperedDirectlyInTheRow()
    {
        var (scope, wave, execution) = await SeedWaveAndExecutionAsync("binding-tampered.pst");
        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        await TamperBindingHashAsync(scope, wave.Id);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIntegrityViolationException>(() =>
            Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None));
    }

    private async Task TamperBindingHashAsync(TenantScope scope, WaveId wave)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(
            "UPDATE dbo.wave_partition_output_bindings SET binding_hash = REPLICATE('0', 64) WHERE wave_id = @wave AND project_id = @project;",
            connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
