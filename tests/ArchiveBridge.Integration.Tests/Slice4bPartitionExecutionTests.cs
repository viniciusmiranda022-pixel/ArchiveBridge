using System.Data;
using System.Globalization;
using System.Text.Json;
using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4B, Passo 3 (SQL Server real) — Partition Execution Foundation: materialização do único caso
/// autorizado (<see cref="PartitionPlanReason.SinglePartWithinTarget"/>) como cópia byte-for-byte verificada,
/// idempotência/concorrência convergentes, staleness/inelegibilidade fail-closed com zero efeito externo,
/// crash-safety em torno dos checkpoints (temp file, rename/finalização, persist), detecção de adulteração
/// sem sobrescrita automática, reconciliação segura de staging órfão, anti-IDOR e ausência de PII no
/// manifesto persistido.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4bPartitionExecutionTests(SqlServerFixture fixture)
{
    private async Task<TenantScope> NewProjectScopeAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture)
            .AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        return scope;
    }

    /// <summary>Registra custódia, inspeciona e planeja (SinglePartWithinTarget) um PST de teste.</summary>
    private async Task<(TenantScope Scope, ArtifactId Artifact, PartitionPlan Plan, byte[] Bytes)> RegisterInspectAndPlanAsync(
        byte[] bytes, string name)
    {
        var scope = await NewProjectScopeAsync();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, name, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(bytes), bytes.Length, CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        return (scope, artifact.Id, plan, bytes);
    }

    private async Task<T> ScalarAsync<T>(TenantScope scope, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
    }

    private Task<int> ExecutionRowCountAsync(TenantScope scope, PartitionPlanId plan) =>
        ScalarAsync<int>(
            scope, "SELECT COUNT(*) FROM dbo.pst_partition_executions WHERE plan_id = @plan AND project_id = @project;",
            ("@plan", plan.Value), ("@project", scope.Project.Value));

    private string FinalOutputDir(TenantScope scope, PartitionPlan plan) =>
        Path.Combine(PlanOutputDir(scope, plan), plan.Parts[0].PartKey.Value);

    private string PlanOutputDir(TenantScope scope, PartitionPlan plan) =>
        Path.Combine(
            Slice4bPstProcessingSupport.PartitionOutputRoot(fixture), scope.Tenant.Value.ToString("N"),
            scope.Project.Value.ToString("N"), plan.Id.Value.ToString("N"));

    // ---- Caminho feliz: materializa, verifica e persiste ----

    [Fact]
    public async Task SinglePartWithinTargetProducesAByteForByteVerifiedOutputAndLeavesTheSourceUntouched()
    {
        var (scope, artifact, plan, bytes) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-happy.pst");

        var execution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(plan.Source.SourceHash, execution.OutputHash);
        Assert.Equal(bytes.Length, execution.OutputSizeBytes);
        Assert.Equal(plan.PlanHash, execution.PlanHash);
        Assert.Equal(plan.Id, execution.Plan);
        Assert.Equal(plan.Parts[0].Id, execution.Part);
        Assert.True(execution.HasConsistentIdentity());

        // A origem permanece byte-for-byte: o Passo 3 é uma CÓPIA, nunca um move/rewrite da origem.
        var root = Slice4bPstProcessingSupport.PstRoot(fixture);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "execute-happy.pst")));

        // O output existe no caminho canônico determinístico e confere byte-for-byte com a origem.
        var outputRoot = Slice4bPstProcessingSupport.PartitionOutputRoot(fixture);
        var finalDir = Path.Combine(
            outputRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"), plan.Parts[0].PartKey.Value);
        Assert.True(Directory.Exists(finalDir));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(finalDir, "part.pst")));
        Assert.Equal(3, Directory.GetFiles(finalDir).Length); // part.pst + part.sha256 + manifest.json, nada mais.

        // Persistido e relegível.
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
        var reread = await Slice4bPstProcessingSupport.ExecutionStore(fixture)
            .FindCanonicalAsync(scope, plan.Id, plan.Parts[0].Id, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal(execution.Id, reread!.Id);
    }

    // ---- Idempotência / concorrência ----

    [Fact]
    public async Task IdempotentReplayReturnsTheSameCanonicalExecutionWithoutRewritingTheOutput()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-replay.pst");
        var useCase = Slice4bPstProcessingSupport.ExecuteUseCase(fixture);

        var first = await useCase.ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        var finalDir = Path.Combine(
            Slice4bPstProcessingSupport.PartitionOutputRoot(fixture), scope.Tenant.Value.ToString("N"),
            scope.Project.Value.ToString("N"), plan.Id.Value.ToString("N"), plan.Parts[0].PartKey.Value);
        var writeTimeAfterFirst = File.GetLastWriteTimeUtc(Path.Combine(finalDir, "part.pst"));
        var shaWriteTimeAfterFirst = File.GetLastWriteTimeUtc(Path.Combine(finalDir, "part.sha256"));
        var manifestWriteTimeAfterFirst = File.GetLastWriteTimeUtc(Path.Combine(finalDir, "manifest.json"));

        // O réplay revalida (somente leitura) o bundle físico contra o registro persistido (AB-4B-007) antes
        // de devolvê-lo — mas essa revalidação em si nunca escreve nada.
        var second = await useCase.ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(writeTimeAfterFirst, File.GetLastWriteTimeUtc(Path.Combine(finalDir, "part.pst"))); // nunca reescrito.
        Assert.Equal(shaWriteTimeAfterFirst, File.GetLastWriteTimeUtc(Path.Combine(finalDir, "part.sha256")));
        Assert.Equal(manifestWriteTimeAfterFirst, File.GetLastWriteTimeUtc(Path.Combine(finalDir, "manifest.json")));
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
    }

    [Fact]
    public async Task ConcurrentExecutionOfTheSamePlanConvergesToExactlyOneCanonicalResult()
    {
        var (scope, _, plan, bytes) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-concurrent.pst");

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None)));

        Assert.Single(results.Select(execution => execution.Id).Distinct());
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var finalDir = Path.Combine(
            Slice4bPstProcessingSupport.PartitionOutputRoot(fixture), scope.Tenant.Value.ToString("N"),
            scope.Project.Value.ToString("N"), plan.Id.Value.ToString("N"), plan.Parts[0].PartKey.Value);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(finalDir, "part.pst")));
    }

    [Fact]
    public async Task ACrashAfterFinalizationButBeforeThePersistCheckpointConvergesWithoutDuplicating()
    {
        var (scope, artifact, plan, bytes) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-crash-before-persist.pst");
        var custody = await Slice4bPstProcessingSupport.CustodyStore(fixture).FindAsync(scope, artifact, CancellationToken.None);

        // Simula o writer tendo terminado (temp file + rename/finalização) mas o processo caiu ANTES do
        // checkpoint 3 (persist SQL): chama o writer DIRETAMENTE, sem passar pelo caso de uso.
        var writer = Slice4bPstProcessingSupport.Writer(fixture);
        var directContext = new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow);
        var direct = await writer.ExecuteAsync(scope, custody!, plan, plan.Parts[0], directContext, CancellationToken.None);
        Assert.Equal(plan.Source.SourceHash, direct.OutputHash);
        Assert.Equal(plan.Source.SourceHash, direct.SourceHash);
        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id)); // ainda nenhum checkpoint SQL.

        // "Restart": o caso de uso completo roda do zero. O writer converge para o MESMO output (nunca
        // reescreve) e só então o checkpoint SQL é persistido.
        var execution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture, writer: writer)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(bytes.Length, execution.OutputSizeBytes);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id)); // nenhuma duplicidade.
    }

    // ---- Elegibilidade e staleness: zero efeito externo ----

    [Fact]
    public async Task AnUnsupportedPlanIsRejectedWithZeroOutputAndZeroExecutionRows()
    {
        var scope = await NewProjectScopeAsync();
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, "execute-unsupported.pst", bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(bytes), bytes.Length, CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        var tinyPolicy = PartitionPolicy.Create(bytes.Length - 1, bytes.Length * 2); // força Unsupported.
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture, tinyPolicy)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PartitionPlanOutcome.Unsupported, plan.Outcome); // pré-condição.

        await Assert.ThrowsAsync<PartitionExecutionNotEligibleException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id));
        Assert.False(Directory.Exists(PlanOutputDir(scope, plan))); // nem sequer a pasta do PLANO existe.
    }

    [Fact]
    public async Task ABlockedPlanIsRejectedWithZeroOutputAndZeroExecutionRows()
    {
        var scope = await NewProjectScopeAsync();
        var bytes = Slice4bPstProcessingSupport.InvalidSignatureBytes();
        var relative = Slice4bPstProcessingSupport.WriteFile(fixture, "execute-blocked.pst", bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(bytes), bytes.Length, CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PartitionPlanOutcome.Blocked, plan.Outcome); // pré-condição: PST estruturalmente inválido.

        await Assert.ThrowsAsync<PartitionExecutionNotEligibleException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id));
    }

    [Fact]
    public async Task SourceThatDriftedOnDiskAfterPlanningIsRejectedBeforeAnyCanonicalOutputIsPublished()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-stale.pst");

        // A custódia (MigrationArtifact) é imutável e nunca é reescrita — mas o ARQUIVO FÍSICO sob a raiz de
        // custódia pode, na prática, ter mudado desde o planejamento (mesmo tamanho, conteúdo diferente). O
        // writer sempre lê a origem AO VIVO e recalcula o hash durante a cópia: a divergência contra
        // plan.Source.SourceHash é detectada e falha fechado ANTES de qualquer output canônico.
        File.WriteAllBytes(
            Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "execute-stale.pst"),
            Slice4bPstProcessingSupport.UnsupportedVersionHeader(totalSize: 4096));

        await Assert.ThrowsAsync<PartitionExecutionSourceStaleException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id));
        Assert.False(Directory.Exists(FinalOutputDir(scope, plan))); // nenhum output canônico publicado.
    }

    // ---- Anti-IDOR ----

    [Fact]
    public async Task CrossTenantAndCrossProjectExecutionIsDeniedIndistinguishablyFromNotFound()
    {
        var (ownerScope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-idor.pst");
        var useCase = Slice4bPstProcessingSupport.ExecuteUseCase(fixture);

        var crossProject = new TenantScope(ownerScope.Tenant, SqlServerFixture.NewScope().Project);
        var crossTenant = new TenantScope(SqlServerFixture.NewScope().Tenant, ownerScope.Project);

        await Assert.ThrowsAsync<PartitionPlanNotFoundException>(() =>
            useCase.ExecuteAsync(crossProject, plan.Id, CorrelationId.New(), CancellationToken.None));
        await Assert.ThrowsAsync<PartitionPlanNotFoundException>(() =>
            useCase.ExecuteAsync(crossTenant, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(0, await ExecutionRowCountAsync(ownerScope, plan.Id));
    }

    // ---- Espaço em disco (cancelamento/timeout são cobertos, determinísticos, em
    // LocalSinglePartExecutionWriterLimitEnforcementTests) ----

    [Fact]
    public async Task InsufficientDiskSpacePreflightFailsClosedWithoutWritingAnything()
    {
        var (scope, artifact, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-nospace.pst");
        var custody = await Slice4bPstProcessingSupport.CustodyStore(fixture).FindAsync(scope, artifact, CancellationToken.None);

        var starvedWriter = new LocalSinglePartExecutionWriter(
            Slice4bPstProcessingSupport.DefaultOptions(fixture),
            new PartitionExecutionOutputOptions
            {
                RootPath = Slice4bPstProcessingSupport.PartitionOutputRoot(fixture),
                MinFreeSpaceMarginBytes = long.MaxValue / 2, // impossível de satisfazer em qualquer disco real.
            },
            new SystemClock());

        var exception = await Assert.ThrowsAsync<PartitionExecutionLimitExceededException>(() =>
            starvedWriter.ExecuteAsync(
                scope, custody!, plan, plan.Parts[0], new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));
        Assert.Equal("INSUFFICIENT_SPACE", exception.ReasonCode);
        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id));
    }

    // ---- Adulteração: nunca sobrescreve automaticamente ----

    [Fact]
    public async Task TamperingTheExistingOutputIsDetectedOnReadbackAndNeverSilentlyOverwritten()
    {
        var (scope, artifact, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-tamper.pst");
        var custody = await Slice4bPstProcessingSupport.CustodyStore(fixture).FindAsync(scope, artifact, CancellationToken.None);

        // Materializa via o writer diretamente (sem persistir o checkpoint SQL — simula o estado "output já
        // existe no caminho final" sob o qual o writer sempre reconfere antes de reaproveitar).
        var writer = Slice4bPstProcessingSupport.Writer(fixture);
        var writerContext = new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow);
        await writer.ExecuteAsync(scope, custody!, plan, plan.Parts[0], writerContext, CancellationToken.None);

        var finalDir = FinalOutputDir(scope, plan);
        var partPath = Path.Combine(finalDir, "part.pst");
        var tampered = "TAMPERED"u8.ToArray();
        File.WriteAllBytes(partPath, tampered);

        // Readback via o writer (o MESMO caminho que a Application percorreria se replay chegasse a tocar o
        // filesystem antes de um checkpoint SQL existir) detecta a adulteração e recusa reaproveitar.
        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            writer.ExecuteAsync(scope, custody!, plan, plan.Parts[0], writerContext, CancellationToken.None));

        // Nunca sobrescrito automaticamente: os bytes adulterados continuam lá, exatamente como estavam.
        Assert.Equal(tampered, File.ReadAllBytes(partPath));
        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id)); // nenhum checkpoint SQL foi gravado nesse fluxo.

        // O mesmo cenário via o caso de uso COMPLETO (sem checkpoint SQL ainda, mas com o output final já
        // adulterado no disco) também falha fechado — nunca publica um checkpoint sobre um output que não
        // confere.
        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture, writer: writer)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));
        Assert.Equal(tampered, File.ReadAllBytes(partPath));
        Assert.Equal(0, await ExecutionRowCountAsync(scope, plan.Id));
    }

    // ---- Correção AB-4B-007: réplay de um checkpoint JÁ PERSISTIDO revalida o bundle físico ANTES de
    // reaproveitá-lo — tampering/corrupção/remoção depois que o checkpoint SQL existe nunca é mascarado. ----

    [Fact]
    public async Task ReplayAfterThePersistedCheckpointDetectsByteAlterationOfThePartFileAndFailsClosed()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-replay-tamper-bytes.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id)); // checkpoint SQL já existe.

        var partPath = Path.Combine(FinalOutputDir(scope, plan), "part.pst");
        var tampered = (byte[])File.ReadAllBytes(partPath).Clone();
        tampered[0] ^= 0xFF; // uma alteração mínima de byte já é suficiente para divergir do hash persistido.
        File.WriteAllBytes(partPath, tampered);

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        // Fail-closed, nunca reconstruído: os bytes adulterados continuam lá e nenhuma linha nova foi gravada.
        Assert.Equal(tampered, File.ReadAllBytes(partPath));
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
    }

    [Theory]
    [InlineData("part.pst")]
    [InlineData("part.sha256")]
    [InlineData("manifest.json")]
    public async Task ReplayAfterThePersistedCheckpointDetectsARemovedBundleFileAndFailsClosedWithoutRecreatingIt(
        string removedFileName)
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), $"execute-replay-missing-{removedFileName}.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var finalDir = FinalOutputDir(scope, plan);
        File.Delete(Path.Combine(finalDir, removedFileName));

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        // Nenhuma verificação de réplay recria o arquivo ausente — a revalidação é estritamente somente leitura.
        Assert.False(File.Exists(Path.Combine(finalDir, removedFileName)));
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id)); // nenhuma linha nova.
    }

    [Fact]
    public async Task ReplayAfterThePersistedCheckpointDetectsManifestOnlyTamperingEvenWithAValidPartAndSidecar()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-replay-tamper-manifest.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var finalDir = FinalOutputDir(scope, plan);
        var partPath = Path.Combine(finalDir, "part.pst");
        var shaPath = Path.Combine(finalDir, "part.sha256");
        var manifestPath = Path.Combine(finalDir, "manifest.json");
        var partBeforeTampering = File.ReadAllBytes(partPath);
        var shaBeforeTampering = await File.ReadAllTextAsync(shaPath);

        // Adultera SOMENTE o manifesto (troca a sequência da parte persistida) — part.pst e part.sha256
        // permanecem exatamente como o writer os deixou e continuam consistentes ENTRE si.
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var tamperedManifest = manifestJson.Replace("\"PartSequence\":1", "\"PartSequence\":2", StringComparison.Ordinal);
        Assert.NotEqual(manifestJson, tamperedManifest); // pré-condição: a substituição realmente mudou o conteúdo.
        await File.WriteAllTextAsync(manifestPath, tamperedManifest);

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        // part.pst e part.sha256 nunca são tocados por essa checagem; o manifesto adulterado também não é
        // silenciosamente corrigido — fica exatamente como o atacante/corrupção o deixou.
        Assert.Equal(partBeforeTampering, File.ReadAllBytes(partPath));
        Assert.Equal(shaBeforeTampering, await File.ReadAllTextAsync(shaPath));
        Assert.Equal(tamperedManifest, await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
    }

    // ---- Correção AB-4B-008: o manifesto físico carrega a lineage mínima exigida pelo work order AB-4B-006
    // item 7 (source_hash, correlation_id, started_at_utc, completed_at_utc) — autoconsistente como evidência
    // local, revalidada por igualdade estrita contra o checkpoint SQL no réplay (nunca "concorda consigo
    // mesmo"), e falha fechado quando adulterada, ausente ou malformada. ----

    [Fact]
    public async Task ThePhysicalManifestContainsTheMinimumLineageFieldsMatchingThePersistedCheckpoint()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-lineage-fields.pst");

        var execution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        var manifest = await ReadManifestNodeAsync(ManifestPath(scope, plan));
        Assert.Equal(execution.SourceHash.Value, manifest["SourceHash"]!.GetValue<string>());
        Assert.Equal(execution.Correlation.Value, Guid.Parse(manifest["CorrelationId"]!.GetValue<string>()));
        Assert.Equal(execution.StartedAtUtc, manifest["StartedAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(execution.CompletedAtUtc, manifest["CompletedAtUtc"]!.GetValue<DateTimeOffset>());

        // source_hash é gravado explicitamente mesmo sendo, neste Passo, sempre igual a output_hash
        // (invariante SinglePartWithinTarget) — preserva a semântica de lineage sem ambiguidade (item 5).
        Assert.Equal(execution.OutputHash.Value, manifest["SourceHash"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("SourceHash")]
    [InlineData("CorrelationId")]
    [InlineData("StartedAtUtc")]
    [InlineData("CompletedAtUtc")]
    public async Task ReplayAfterThePersistedCheckpointDetectsIsolatedLineageFieldTamperingAndFailsClosed(string fieldName)
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), $"execute-replay-tamper-lineage-{fieldName}.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var manifestPath = ManifestPath(scope, plan);
        var node = await ReadManifestNodeAsync(manifestPath);
        var original = node[fieldName]!.GetValue<string>();
        node[fieldName] = TamperedLineageValue(fieldName, original);
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        // Fail-closed, nunca reescrito: o manifesto adulterado continua lá e nenhuma linha nova foi gravada.
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
    }

    [Theory]
    [InlineData("SourceHash")]
    [InlineData("CorrelationId")]
    [InlineData("StartedAtUtc")]
    [InlineData("CompletedAtUtc")]
    public async Task ReplayAfterThePersistedCheckpointDetectsAMissingLineageFieldAndFailsClosed(string fieldName)
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), $"execute-replay-missing-lineage-{fieldName}.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var manifestPath = ManifestPath(scope, plan);
        var node = await ReadManifestNodeAsync(manifestPath);
        Assert.True(node.Remove(fieldName)); // pré-condição: o campo realmente existia antes da remoção.
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id)); // nenhuma linha nova.
    }

    [Fact]
    public async Task ReplayAfterThePersistedCheckpointDetectsAMalformedCorrelationIdAndFailsClosed()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-replay-malformed-correlation.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var manifestPath = ManifestPath(scope, plan);
        var node = await ReadManifestNodeAsync(manifestPath);
        node["CorrelationId"] = "not-a-guid"; // malformado — nunca um GUID válido diferente, mas texto ilegível.
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
    }

    [Fact]
    public async Task ReplayAfterThePersistedCheckpointDetectsAMalformedTimestampAndFailsClosed()
    {
        var (scope, _, plan, _) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-replay-malformed-timestamp.pst");
        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));

        var manifestPath = ManifestPath(scope, plan);
        var node = await ReadManifestNodeAsync(manifestPath);
        node["StartedAtUtc"] = "not-a-date"; // manifest.json malformado (item 6: "malformed manifest falha fechado").
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        await Assert.ThrowsAsync<PartitionExecutionOutputTamperedException>(() =>
            Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
                .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None));

        Assert.Equal(1, await ExecutionRowCountAsync(scope, plan.Id));
    }

    private static string TamperedLineageValue(string fieldName, string original) => fieldName switch
    {
        "SourceHash" => string.Equals(original, new string('0', 64), StringComparison.Ordinal) ? new string('1', 64) : new string('0', 64),
        "CorrelationId" => Guid.NewGuid().ToString(),
        "StartedAtUtc" or "CompletedAtUtc" => DateTimeOffset.Parse(
                original, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .AddSeconds(1).ToString("O", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(fieldName), fieldName, "Campo de lineage desconhecido no teste."),
    };

    private string ManifestPath(TenantScope scope, PartitionPlan plan) => Path.Combine(FinalOutputDir(scope, plan), "manifest.json");

    private static async Task<System.Text.Json.Nodes.JsonObject> ReadManifestNodeAsync(string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        return System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
    }

    // ---- Órfão de staging: nunca confundido com sucesso; reconciliação segura por idade ----

    [Fact]
    public async Task AnOrphanStagingDirectoryIsNeverConfusedWithACompletedPartAndIsSafelyReconciled()
    {
        var (scope, _, plan, bytes) = await RegisterInspectAndPlanAsync(
            Slice4bPstProcessingSupport.ValidUnicodeHeader(), "execute-orphan.pst");

        var outputRoot = Slice4bPstProcessingSupport.PartitionOutputRoot(fixture);
        var stagingRoot = Path.Combine(outputRoot, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var orphan = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllBytes(Path.Combine(orphan, "part.pst"), [0x01, 0x02, 0x03]); // resíduo de um crash simulado.

        // A execução normal NUNCA olha para o staging alheio — converge normalmente para o resultado correto.
        var execution = await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);
        Assert.Equal(bytes.Length, execution.OutputSizeBytes);
        Assert.True(Directory.Exists(orphan)); // o órfão continua lá — não foi tocado pela execução normal.

        // Regra de reconciliação: um limiar de idade generoso NÃO remove nada ainda (não deveria confiar em
        // timestamps futuros); um limiar já vencido remove o órfão.
        var now = DateTimeOffset.UtcNow;
        var notYetRemoved = TempStagingReconciler.CleanupOrphans(outputRoot, TimeSpan.FromDays(1), now);
        Assert.Empty(notYetRemoved);
        Assert.True(Directory.Exists(orphan));

        var removed = TempStagingReconciler.CleanupOrphans(outputRoot, TimeSpan.Zero, now.AddSeconds(1));
        Assert.Contains(orphan, removed);
        Assert.False(Directory.Exists(orphan));

        // O output canônico do plano nunca é tratado como staging — permanece intacto após a reconciliação.
        var finalDir = Path.Combine(
            outputRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"), plan.Parts[0].PartKey.Value);
        Assert.True(File.Exists(Path.Combine(finalDir, "part.pst")));
    }

    // ---- Minimização de PII no manifesto durável (SQL + manifest.json) ----

    [Fact]
    public async Task ThePersistedExecutionAndManifestCarryNoPathFileNameOrMailboxIdentifier()
    {
        var scope = await NewProjectScopeAsync();
        var bytes = Slice4bPstProcessingSupport.ValidUnicodeHeader();
        Directory.CreateDirectory(Path.Combine(Slice4bPstProcessingSupport.PstRoot(fixture), "mailboxes"));
        const string relative = "mailboxes/bob.example.pst";
        Slice4bPstProcessingSupport.WriteFile(fixture, relative, bytes);
        var artifact = await Slice4bPstProcessingSupport.CustodyStore(fixture).RegisterAsync(
            scope.Tenant, scope.Project, new PstRelativePath(relative),
            DeterministicHash.ComputeBytes(bytes), bytes.Length, CancellationToken.None);
        await Slice4bPstProcessingSupport.UseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);
        var plan = await Slice4bPstProcessingSupport.PlanUseCase(fixture)
            .ExecuteAsync(scope, artifact.Id, CorrelationId.New(), CancellationToken.None);

        await Slice4bPstProcessingSupport.ExecuteUseCase(fixture)
            .ExecuteAsync(scope, plan.Id, CorrelationId.New(), CancellationToken.None);

        foreach (var value in await ReadAllTextValuesAsync(scope, "dbo.pst_partition_executions", plan.Id))
        {
            Assert.DoesNotContain("bob", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mailboxes", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".pst", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Slice4bPstProcessingSupport.PstRoot(fixture), value, StringComparison.OrdinalIgnoreCase);
        }

        var finalDir = Path.Combine(
            Slice4bPstProcessingSupport.PartitionOutputRoot(fixture), scope.Tenant.Value.ToString("N"),
            scope.Project.Value.ToString("N"), plan.Id.Value.ToString("N"), plan.Parts[0].PartKey.Value);
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(finalDir, "manifest.json"));
        using var manifest = JsonDocument.Parse(manifestJson);
        var manifestText = manifestJson;
        Assert.DoesNotContain("bob", manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mailboxes", manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pst", manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), manifestText, StringComparison.Ordinal);
        // Só os campos esperados (IDs opacos, hashes, metadados de execução e lineage mínima do work order
        // AB-4B-006 item 7 — sourceHash/correlationId/startedAtUtc/completedAtUtc, correção AB-4B-008)
        // existem no manifesto.
        var expectedFields = new[]
        {
            "tenant", "project", "plan", "part", "planHash", "partSequence", "partKey", "sourceHash",
            "outputHash", "outputSizeBytes", "executorName", "executorVersion", "correlationId",
            "startedAtUtc", "completedAtUtc",
        };
        foreach (var property in manifest.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, expectedFields, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<List<string>> ReadAllTextValuesAsync(TenantScope scope, string table, PartitionPlanId plan)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        // O nome da tabela vem de uma lista fixa do próprio teste (nunca de entrada externa).
        await using var command = new SqlCommand($"SELECT * FROM {table} WHERE plan_id = @plan;", connection);
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = plan.Value });
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!reader.IsDBNull(index))
                {
                    values.Add(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }

        Assert.True(rows > 0, $"Nenhuma linha encontrada em {table} para o plano.");
        return values;
    }
}
