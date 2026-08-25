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
/// detecção de adulteração do <c>binding_hash</c> diretamente na linha. AB-I5-013 — a correlação com a
/// entrada de destino: múltiplos mailboxes numa mesma onda, múltiplas partes físicas para o mesmo
/// mailbox, troca de entrada/binding IDs, escopo cruzado, reassignação ambígua e adulteração de
/// <c>entry_id</c> diretamente na linha.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class WavePartitionOutputBindingIntegrationTests(SqlServerFixture fixture)
{
    private static readonly IClock Clock = new SystemClock();

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private CreateWavePartitionOutputBindingUseCase UseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    /// <summary>Registra/inspeciona/planeja/executa um PST real de teste e devolve a execução canônica resultante.</summary>
    private async Task<PartitionExecutionRecord> RegisterAndExecuteAsync(TenantScope scope, string name)
    {
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative), DeterministicHash.ComputeBytes(bytes), bytes.Length,
            CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        return await Slice4bPstProcessingSupport.ExecuteUseCase(fixture).ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
    }

    /// <summary>Cria projeto + onda aprovada com uma única entrada e registra/inspeciona/planeja/executa um PST real.</summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, WaveEntry Entry, PartitionExecutionRecord Execution)> SeedWaveAndExecutionAsync(string name)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var execution = await RegisterAndExecuteAsync(scope, name);

        var entry = Slice2Support.Entry(name, "user@contoso.com", execution.OutputSizeBytes);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([entry]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        return (scope, wave, entry, execution);
    }

    [Fact]
    public async Task ThePersistedBindingReidratesExactlyAsSavedAndIsFoundByFindCanonicalAndListForWave()
    {
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-happy.pst");
        var entryId = WaveEntryId.Derive(wave.Id, entry);

        var created = await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        var reread = await Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal(created.Id, reread!.Id);
        Assert.Equal(execution.Id, reread.Execution);
        Assert.Equal(execution.OutputHash, reread.OutputHash);
        Assert.Equal(entryId, reread.Entry);

        var forWave = await Bindings().ListForWaveAsync(scope, wave.Id, CancellationToken.None);
        Assert.Single(forWave);
        Assert.Equal(created.Id, forWave[0].Id);
        Assert.Equal(entryId, forWave[0].Entry);
    }

    [Fact]
    public async Task ARepeatedBindingRequestForTheSameWavePlanAndPartConvergesWithoutDuplicatingUnderRealSql()
    {
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-idempotent.pst");
        var entryId = WaveEntryId.Derive(wave.Id, entry);
        var useCase = UseCase();

        var first = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);
        var second = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.wave_partition_output_bindings WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TwoConcurrentBindingCreationsForTheSameWavePlanAndPartNeverProduceTwoCanonicalRows()
    {
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-race.pst");
        var request = new CreateWavePartitionOutputBindingRequest(
            scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New());

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
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-cross-project.pst");
        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        var otherProjectScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        var fromOtherProject = await Bindings()
            .FindCanonicalAsync(otherProjectScope, wave.Id, execution.Plan, execution.Part, CancellationToken.None);

        Assert.Null(fromOtherProject);
    }

    [Fact]
    public async Task ABindingRequestWithAnEntryIdBelongingToAnotherTenantSWaveIsIndistinguishableFromNotFound()
    {
        // AB-I5-013 item 3, escopo cruzado real: um WaveEntryId calculado corretamente para a onda de OUTRO
        // tenant é recusado com o MESMO erro anti-IDOR — nunca revela que a onda/entrada existe alhures.
        var (scopeA, waveA, entryA, executionA) = await SeedWaveAndExecutionAsync("binding-cross-tenant-a.pst");
        var (scopeB, waveB, entryB, _) = await SeedWaveAndExecutionAsync("binding-cross-tenant-b.pst");
        Assert.NotEqual(scopeA.Tenant, scopeB.Tenant);

        var entryIdFromOtherTenant = WaveEntryId.Derive(waveB.Id, entryB);

        await Assert.ThrowsAsync<WavePartitionOutputBindingSourceNotFoundException>(() =>
            UseCase().ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scopeA, waveA.Id, entryIdFromOtherTenant, executionA.Plan, executionA.Part, CorrelationId.New()),
                CancellationToken.None));
    }

    [Fact]
    public async Task MultipleMailboxesInTheSameWaveEachBindToTheirOwnDistinctEntry()
    {
        // AB-I5-013 item 7: uma onda com VÁRIOS mailboxes — cada PST físico deve se correlacionar
        // corretamente com o SEU PRÓPRIO destino, nunca com o de outro mailbox da mesma onda.
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var executionA = await RegisterAndExecuteAsync(scope, "multi-mailbox-a.pst");
        var executionB = await RegisterAndExecuteAsync(scope, "multi-mailbox-b.pst");
        var entryA = Slice2Support.Entry("multi-mailbox-a.pst", "alice@contoso.com", executionA.OutputSizeBytes);
        var entryB = Slice2Support.Entry("multi-mailbox-b.pst", "bob@contoso.com", executionB.OutputSizeBytes);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([entryA, entryB]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var entryIdA = WaveEntryId.Derive(wave.Id, entryA);
        var entryIdB = WaveEntryId.Derive(wave.Id, entryB);
        var useCase = UseCase();

        var boundA = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryIdA, executionA.Plan, executionA.Part, CorrelationId.New()),
            CancellationToken.None);
        var boundB = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryIdB, executionB.Plan, executionB.Part, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(entryIdA, boundA.Entry);
        Assert.Equal(entryIdB, boundB.Entry);
        Assert.NotEqual(boundA.Entry, boundB.Entry);
        Assert.Equal(executionA.Artifact, boundA.Artifact);
        Assert.Equal(executionB.Artifact, boundB.Artifact);
    }

    [Fact]
    public async Task MultiplePhysicalPartsForTheSameMailboxAllBindToTheSameEntryWithoutAmbiguity()
    {
        // AB-I5-013 item 7: um mailbox com MÚLTIPLAS partes físicas (PST grande particionado) — todas as
        // partes convergem para a MESMA entrada de destino, sem serem tratadas como reassignação ambígua.
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var executionPart1 = await RegisterAndExecuteAsync(scope, "multi-part-1.pst");
        var executionPart2 = await RegisterAndExecuteAsync(scope, "multi-part-2.pst");
        var entry = Slice2Support.Entry(
            "multi-part.pst", "carol@contoso.com", executionPart1.OutputSizeBytes + executionPart2.OutputSizeBytes);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([entry]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var entryId = WaveEntryId.Derive(wave.Id, entry);
        var useCase = UseCase();

        var boundPart1 = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, executionPart1.Plan, executionPart1.Part, CorrelationId.New()),
            CancellationToken.None);
        var boundPart2 = await useCase.ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(scope, wave.Id, entryId, executionPart2.Plan, executionPart2.Part, CorrelationId.New()),
            CancellationToken.None);

        Assert.Equal(entryId, boundPart1.Entry);
        Assert.Equal(entryId, boundPart2.Entry);
        Assert.NotEqual(boundPart1.Id, boundPart2.Id);

        var forWave = await Bindings().ListForWaveAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(2, forWave.Count);
        Assert.All(forWave, binding => Assert.Equal(entryId, binding.Entry));
    }

    [Fact]
    public async Task ReassigningTheSamePhysicalArtifactToADifferentEntryInTheSameWaveFailsClosedUnderRealSql()
    {
        // AB-I5-013 item 4, sob SQL real: o mesmo artefato físico, replanejado sob um NOVO plano/parte,
        // não pode ser vinculado a uma entrada diferente da já canonicamente vinculada nesta onda.
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, "reassign.pst", bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative), DeterministicHash.ComputeBytes(bytes), bytes.Length,
            CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        var firstPlan = await Slice4bPstProcessingSupport.PlanUseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var firstExecution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture).ExecuteAsync(scope, firstPlan.Id, CorrelationId.New(), CancellationToken.None);

        var entryA = Slice2Support.Entry("reassign-a.pst", "alice@contoso.com", bytes.Length);
        var entryB = Slice2Support.Entry("reassign-b.pst", "bob@contoso.com", bytes.Length);
        var wave = Slice2Support.NewWave(scope, new WaveSelection([entryA, entryB]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entryA), firstExecution.Plan, firstExecution.Part, CorrelationId.New()),
            CancellationToken.None);

        // Replaneja o MESMO artefato — um novo plano/parte canônico para o mesmo ArtifactId.
        var secondPlan = await Slice4bPstProcessingSupport.PlanUseCase(fixture).ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var secondExecution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture).ExecuteAsync(scope, secondPlan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(firstExecution.Artifact, secondExecution.Artifact);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIncompatibleException>(() =>
            UseCase().ExecuteAsync(
                new CreateWavePartitionOutputBindingRequest(
                    scope, wave.Id, WaveEntryId.Derive(wave.Id, entryB), secondExecution.Plan, secondExecution.Part, CorrelationId.New()),
                CancellationToken.None));

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.wave_partition_output_bindings WHERE wave_id = @wave;",
            ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetCanonicalFailsClosedWhenTheBindingHashIsTamperedDirectlyInTheRow()
    {
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-tampered.pst");
        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        await TamperBindingHashAsync(scope, wave.Id);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIntegrityViolationException>(() =>
            Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None));
    }

    [Fact]
    public async Task GetCanonicalFailsClosedWhenTheEntryIdColumnIsTamperedDirectlyInTheRow()
    {
        // AB-I5-013 item 5: entry_id faz parte do binding_hash — adulterar SOMENTE essa coluna diretamente
        // no banco (sem tocar em nenhuma outra) deve ser detectado no rehydrate, não silenciosamente aceito.
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-entry-tampered.pst");
        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        await TamperEntryIdAsync(scope, wave.Id);

        await Assert.ThrowsAsync<WavePartitionOutputBindingIntegrityViolationException>(() =>
            Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None));
    }

    [Fact]
    public async Task RehydrationIsDeterministicAcrossMultipleReadsOfTheSameCanonicalRow()
    {
        // AB-I5-013 item 7 "deterministic rehydration": ler a mesma linha canônica repetidamente sempre
        // recomputa o MESMO binding_hash e devolve os mesmos campos, sem depender de estado em memória.
        var (scope, wave, entry, execution) = await SeedWaveAndExecutionAsync("binding-deterministic.pst");
        await UseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        var first = await Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None);
        var second = await Bindings().FindCanonicalAsync(scope, wave.Id, execution.Plan, execution.Part, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first, second);
        Assert.Equal(first!.BindingHash, second!.BindingHash);
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

    private async Task TamperEntryIdAsync(TenantScope scope, WaveId wave)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(
            "UPDATE dbo.wave_partition_output_bindings SET entry_id = REPLICATE('a', 64) WHERE wave_id = @wave AND project_id = @project;",
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
