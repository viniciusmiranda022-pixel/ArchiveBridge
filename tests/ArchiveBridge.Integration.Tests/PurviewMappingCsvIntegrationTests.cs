using System.Data;
using System.Text;
using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.WavePartitionBindings;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I5-012 (SQL Server real) — o builder do mapping CSV do Purview de ponta a ponta: resolução de
/// evidência canônica (vínculo + execução + precheck de mailbox + upload verificado), geração/persistência
/// versionada com o protocolo recuperável em duas fases, idempotência sob reaproveitamento, nova versão
/// sob mudança real de evidência, isolamento cross-project (RLS/anti-IDOR) e download por referência
/// opaca com revalidação de hash contra a evidência SQL.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class PurviewMappingCsvIntegrationTests(SqlServerFixture fixture)
{
    private static readonly SystemClock Clock = new();

    private SqlWavePartitionOutputBindingStore Bindings() => new(fixture.Factory);

    private SqlPurviewUploadRequestStore UploadRequests() => new(fixture.Factory, Clock);

    private SqlPurviewUploadAttemptStore UploadAttempts() => new(fixture.Factory);

    private SqlMailboxPrecheckStore Prechecks() => new(fixture.Factory);

    private SqlPurviewMappingCsvStore MappingStore() => new(fixture.Factory, Clock);

    private FileSystemMappingArtifactStore Artifacts() =>
        new(Path.Combine(fixture.ArtifactRoot, "purview-mapping-" + Guid.NewGuid().ToString("N")));

    private CreateWavePartitionOutputBindingUseCase BindingUseCase() =>
        new(Slice2Support.WaveStore(fixture), Slice4bPstProcessingSupport.ExecutionStore(fixture), Bindings(), Clock);

    private ResolvePurviewMappingEvidenceUseCase EvidenceResolver() => new(
        Slice2Support.WaveStore(fixture), Bindings(), Slice4bPstProcessingSupport.ExecutionStore(fixture),
        UploadRequests(), UploadAttempts(), Prechecks());

    private GeneratePurviewMappingCsvUseCase GenerateUseCase(FileSystemMappingArtifactStore artifacts) =>
        new(EvidenceResolver(), MappingStore(), artifacts, Clock);

    /// <summary>Registra/inspeciona/planeja/executa um PST real e devolve a execução canônica resultante.</summary>
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

    /// <summary>Persiste o precheck de mailbox com o status de archive informado (identidade já resolvida pela entrada).</summary>
    private async Task SeedPrecheckAsync(TenantScope scope, WaveEntry entry, MailboxArchiveStatus status)
    {
        var snapshot = MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), scope.Tenant, scope.Project, entry.Archive, version: 1,
            exchangeGuid: Guid.NewGuid(), archiveGuid: status == MailboxArchiveStatus.Active ? Guid.NewGuid() : null,
            status, "UserMailbox", autoExpandingArchiveEnabled: false, litigationHoldEnabled: false, retentionHoldEnabled: false,
            archiveItemCount: 10, archiveTotalSizeBytes: 4096, observedAvailableBytes: 100_000_000_000,
            DateTimeOffset.UtcNow, CorrelationId.New(), DateTimeOffset.UtcNow);
        await Prechecks().AppendAsync(snapshot, CancellationToken.None);
    }

    /// <summary>Marca o upload da onda como verificado (Uploaded) com a evidência exatamente coerente com os vínculos informados.</summary>
    private async Task MarkUploadVerifiedAsync(TenantScope scope, MigrationWave wave, IReadOnlyList<PartitionExecutionRecord> executions)
    {
        var enqueue = await UploadRequests().EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);
        var jobs = new ArchiveBridge.Infrastructure.Jobs.SqlJobStore(fixture.Factory, Clock, agingInterval: TimeSpan.FromSeconds(30));
        var claimed = await jobs.TryClaimNextAsync(
            new ArchiveBridge.Contracts.Jobs.ClaimRequest(
                scope, ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload, new ArchiveBridge.Domain.Jobs.WorkerId("test-worker"),
                TimeSpan.FromMinutes(5), CorrelationId.New()),
            CancellationToken.None);
        Assert.NotNull(claimed);
        var fence = new JobFence(scope, claimed!.JobId, new ArchiveBridge.Domain.Jobs.WorkerId("test-worker"), claimed.Epoch);

        var now = Clock.UtcNow;
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            expectedFileCount: executions.Count,
            expectedTotalBytes: executions.Sum(execution => execution.OutputSizeBytes),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id));
        var record = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, now, now);
        await UploadAttempts().AppendAsync(scope, record, fence, CancellationToken.None);
    }

    /// <summary>Constrói o cenário completo e verificado (onda aprovada, 1 entrada, PST executado, vínculo, upload verificado, precheck Active).</summary>
    private async Task<(TenantScope Scope, MigrationWave Wave, WaveEntry Entry, PartitionExecutionRecord Execution)> SeedVerifiedSingleEntryWaveAsync(
        string name, string mailbox, MailboxArchiveStatus precheckStatus = MailboxArchiveStatus.Active)
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var execution = await RegisterAndExecuteAsync(scope, name);
        var entry = Slice2Support.Entry(name, mailbox, execution.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entry])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);

        await SeedPrecheckAsync(scope, entry, precheckStatus);
        await MarkUploadVerifiedAsync(scope, wave, [execution]);

        return (scope, wave, entry, execution);
    }

    [Fact]
    public async Task GenerateProducesAUsableVersionWithTheExpectedRowDerivedFromRealUploadAndPrecheckEvidence()
    {
        var (scope, wave, entry, execution) = await SeedVerifiedSingleEntryWaveAsync(
            "e2e-happy.pst", "alice-happy@contoso.com", MailboxArchiveStatus.Active);
        var artifacts = Artifacts();

        var outcome = await GenerateUseCase(artifacts).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        Assert.True(outcome.Regenerated);
        Assert.Equal(1, outcome.Document.RowCount);

        var text = Encoding.UTF8.GetString(outcome.Document.Bytes);
        var dataLine = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        var fields = dataLine.Split(',');
        Assert.Equal("Exchange", fields[0]);
        Assert.Equal(PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id).WaveSegment, fields[1]);
        Assert.Equal(PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value, fields[2]);
        Assert.Equal("alice-happy@contoso.com", fields[3]);
        Assert.Equal("TRUE", fields[4]);
        Assert.Equal(wave.TargetRootFolder.Value, fields[5]);
        Assert.Equal(string.Empty, fields[6]);

        var usable = await MappingStore().GetUsableAsync(scope, wave.Id, CancellationToken.None);
        Assert.NotNull(usable);
        Assert.Equal(outcome.Version.ContentSha256, usable!.ContentSha256);
        Assert.Equal(MappingVersionStatus.Usable, usable.Status);
    }

    [Fact]
    public async Task IsArchiveIsFalseWhenTheMailboxPrecheckDoesNotComproveAnActiveArchive()
    {
        var (scope, wave, _, _) = await SeedVerifiedSingleEntryWaveAsync(
            "e2e-inactive.pst", "bob-inactive@contoso.com", MailboxArchiveStatus.Disabled);
        var artifacts = Artifacts();

        var outcome = await GenerateUseCase(artifacts).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        var text = Encoding.UTF8.GetString(outcome.Document.Bytes);
        var dataLine = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Equal("FALSE", dataLine.Split(',')[4]);
    }

    [Fact]
    public async Task GenerateFailsClosedWhenTheWaveHasNoCanonicalBindingsYet()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(
            scope, new WaveSelection([Slice2Support.Entry("no-binding.pst", "nobinding@contoso.com", 4096)])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await Assert.ThrowsAsync<PurviewMappingCsvGenerationException>(() =>
            GenerateUseCase(Artifacts()).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None));
    }

    [Fact]
    public async Task GenerateFailsClosedWhenTheUploadWasNeverRequestedForTheWave()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var execution = await RegisterAndExecuteAsync(scope, "e2e-no-upload.pst");
        var entry = Slice2Support.Entry("e2e-no-upload.pst", "noupload@contoso.com", execution.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entry])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entry), execution.Plan, execution.Part, CorrelationId.New()),
            CancellationToken.None);
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);

        await Assert.ThrowsAsync<PurviewMappingCsvGenerationException>(() =>
            GenerateUseCase(Artifacts()).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None));
    }

    [Fact]
    public async Task GenerateFailsClosedWhenTheVerifiedUploadEvidenceHasDriftedFromTheCurrentBindings()
    {
        // Onda com DOIS PSTs vinculados, mas a evidência de upload verificada só cobre UM (drift real:
        // ex. um novo vínculo foi criado depois do upload verificado) — recusado fail-closed.
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);

        var executionA = await RegisterAndExecuteAsync(scope, "drift-a.pst");
        var executionB = await RegisterAndExecuteAsync(scope, "drift-b.pst");
        var entryA = Slice2Support.Entry("drift-a.pst", "drift-a@contoso.com", executionA.OutputSizeBytes);
        var entryB = Slice2Support.Entry("drift-b.pst", "drift-b@contoso.com", executionB.OutputSizeBytes);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([entryA, entryB])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entryA), executionA.Plan, executionA.Part, CorrelationId.New()),
            CancellationToken.None);
        await SeedPrecheckAsync(scope, entryA, MailboxArchiveStatus.Active);
        await SeedPrecheckAsync(scope, entryB, MailboxArchiveStatus.Active);

        // Evidência de upload verificada cobre SOMENTE o binding A (1 arquivo) — coerente no momento do upload.
        await MarkUploadVerifiedAsync(scope, wave, [executionA]);

        // Um SEGUNDO vínculo é criado DEPOIS do upload verificado (cenário de drift real).
        await BindingUseCase().ExecuteAsync(
            new CreateWavePartitionOutputBindingRequest(
                scope, wave.Id, WaveEntryId.Derive(wave.Id, entryB), executionB.Plan, executionB.Part, CorrelationId.New()),
            CancellationToken.None);

        await Assert.ThrowsAsync<PurviewMappingCsvGenerationException>(() =>
            GenerateUseCase(Artifacts()).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None));
    }

    [Fact]
    public async Task ARepeatedGenerationWithNoRealEvidenceChangeReusesTheSameVersionWithoutRegenerating()
    {
        var (scope, wave, _, _) = await SeedVerifiedSingleEntryWaveAsync("e2e-idempotent.pst", "idem@contoso.com");
        var artifacts = Artifacts();
        var useCase = GenerateUseCase(artifacts);

        var first = await useCase.ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        var second = await useCase.ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        Assert.True(first.Regenerated);
        Assert.False(second.Regenerated);
        Assert.Equal(first.Version.Version, second.Version.Version);
        Assert.Equal(first.Document.ContentSha256, second.Document.ContentSha256);

        var count = await CountAsync(scope, "SELECT COUNT(*) FROM dbo.purview_mapping_csv_versions WHERE wave_id = @wave;", ("@wave", wave.Id.Value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ARealChangeInMailboxPrecheckProducesANewVersionAndSupersedesThePrevious()
    {
        var (scope, wave, entry, _) = await SeedVerifiedSingleEntryWaveAsync(
            "e2e-version-bump.pst", "bump@contoso.com", MailboxArchiveStatus.Disabled);
        var artifacts = Artifacts();
        var useCase = GenerateUseCase(artifacts);

        var first = await useCase.ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);
        Assert.Equal(1, first.Version.Version.Value);

        // Mudança REAL de evidência: o precheck agora comprova archive ativo — nova versão do snapshot.
        await SeedPrecheckAsync(scope, entry, MailboxArchiveStatus.Active);
        var second = await useCase.ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        Assert.True(second.Regenerated);
        Assert.Equal(2, second.Version.Version.Value);
        Assert.NotEqual(first.Document.ContentSha256, second.Document.ContentSha256);

        var usable = await MappingStore().GetUsableAsync(scope, wave.Id, CancellationToken.None);
        Assert.NotNull(usable);
        Assert.Equal(2, usable!.Version.Value);

        var previous = await MappingStore().GetByVersionAsync(scope, wave.Id, first.Version.Version, CancellationToken.None);
        Assert.NotNull(previous);
        Assert.Equal(MappingVersionStatus.Superseded, previous!.Status); // preservada, nunca apagada.
    }

    [Fact]
    public async Task DownloadReturnsTheExactBytesOfTheRequestedVersionAndFailsClosedForAnotherProject()
    {
        var (scope, wave, _, _) = await SeedVerifiedSingleEntryWaveAsync("e2e-download.pst", "download@contoso.com");
        var artifacts = Artifacts();
        var generated = await GenerateUseCase(artifacts).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        var download = await new DownloadPurviewMappingCsvUseCase(MappingStore(), artifacts)
            .ExecuteAsync(scope, wave.Id, generated.Version.Version, CancellationToken.None);
        Assert.Equal(generated.Document.ContentSha256, download.Version.ContentSha256);
        Assert.Equal(generated.Document.Bytes, download.Bytes);

        var otherProjectScope = new TenantScope(scope.Tenant, new ArchiveBridge.Domain.Projects.ProjectId(Guid.NewGuid()));
        await Assert.ThrowsAsync<PurviewMappingCsvSourceNotFoundException>(() =>
            new DownloadPurviewMappingCsvUseCase(MappingStore(), artifacts)
                .ExecuteAsync(otherProjectScope, wave.Id, generated.Version.Version, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadFailsClosedWhenTheSqlEvidenceHashDivergesFromThePublishedArtifact()
    {
        var (scope, wave, _, _) = await SeedVerifiedSingleEntryWaveAsync("e2e-download-tampered.pst", "tampered@contoso.com");
        var artifacts = Artifacts();
        var generated = await GenerateUseCase(artifacts).ExecuteAsync(scope, wave.Id, "operator", CancellationToken.None);

        await TamperContentHashAsync(scope, wave.Id, generated.Version.Version.Value);

        await Assert.ThrowsAsync<PurviewMappingCsvGenerationException>(() =>
            new DownloadPurviewMappingCsvUseCase(MappingStore(), artifacts)
                .ExecuteAsync(scope, wave.Id, generated.Version.Version, CancellationToken.None));
    }

    private async Task TamperContentHashAsync(TenantScope scope, WaveId wave, int version)
    {
        await using var connection = new SqlConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using (var context = new SqlCommand("EXEC sys.sp_set_session_context @key = N'tenant_id', @value = @tenant;", connection))
        {
            context.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand(
            "UPDATE dbo.purview_mapping_csv_versions SET content_sha256 = REPLICATE('0', 64) " +
            "WHERE wave_id = @wave AND project_id = @project AND mapping_version = @version;",
            connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
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
