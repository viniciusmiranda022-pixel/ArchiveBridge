using System.Text.Json;
using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 3 — descoberta durável sobre SQL real: fluxo enqueue→claim→descoberta read-only→persistência
/// cercada→conclusão; contexto obsoleto bloqueado; reconciliação de quedas pelos TRÊS hashes; versão
/// anterior utilizável até a nova finalizar; worker defasado não persiste; reserva pendente ATÔMICA sob
/// concorrência (uma só versão por evidência); mudança de configuração não reaproveita reserva; isolamento
/// por tenant; evidência imutável.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice3EvDiscoveryTests(SqlServerFixture fixture)
{
    private static readonly WorkerId WorkerA = new("worker-A");
    private static readonly EvDiscoveryPolicy Policy = EvDiscoveryPolicy.Default;
    private const string DocumentedAdapterId = "ev-export-adapter-documented-15-1";

    private async Task<(TenantScope Scope, MigrationProject Project)> SeedProjectAsync()
    {
        var scope = SqlServerFixture.NewScope();
        var project = Slice2Support.NewProject(scope);
        await Slice2Support.ProjectStore(fixture).AddAsync(project, CorrelationId.New(), CancellationToken.None);
        return (scope, project);
    }

    private static EvDiscoveryCommand Command(TenantScope scope, MigrationProject project, EvEnvironmentDescriptor env, Sha256Hash? overrideHash = null) =>
        new(scope, env.EnvironmentId, env.SiteName, env.DirectoryServer, "do", CorrelationId.New(),
            new EvDiscoveryCommandContext(EvDiscoveryCommandContext.CurrentSchemaVersion,
                project.ConfigurationVersion.Value, overrideHash ?? project.ConfigurationHash, EvDiscoveryPolicy.CurrentVersion));

    private Task<EvDiscoveryOutcome> RunUseCaseAsync(
        TenantScope scope, MigrationProject project, EvEnvironmentDescriptor env, FixtureEvPowerShellHost host, IClock clock) =>
        Slice3Support.UseCase(fixture, host, clock).ExecuteAsync(
            scope, env, Policy, project.ConfigurationVersion.Value, project.ConfigurationHash, CorrelationId.New(), CancellationToken.None);

    [Fact]
    public async Task DurableFlowReachesReadyAndUsable()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        await Slice3Support.Inbox(fixture, clock).EnqueueAsync(Command(scope, project, env), CancellationToken.None);

        var execution = await Slice3Support.Processor(fixture, Slice3Support.ReadyHost(), clock)
            .ProcessNextAsync(scope, WorkerA, Slice3Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(EvDiscoveryCommandOutcome.Completed, execution!.Outcome);
        var usable = await Slice3Support.Store(fixture, clock).GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.NotNull(usable);
        Assert.Equal(EvDiscoveryStatus.Ready, usable!.Status);
        Assert.Equal(DocumentedAdapterId, usable.SelectedAdapter!.Value.Value);
        Assert.Equal(EvDiscoveryResultCodes.DiscoveryCompleted, usable.ResultCode.Value);
    }

    [Fact]
    public async Task StaleCommandContextFailsClosed()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var wrongHash = new Sha256Hash(new string('0', 64));
        await Slice3Support.Inbox(fixture, clock).EnqueueAsync(Command(scope, project, env, wrongHash), CancellationToken.None);

        var execution = await Slice3Support.Processor(fixture, Slice3Support.ReadyHost(), clock)
            .ProcessNextAsync(scope, WorkerA, Slice3Support.Lease, CorrelationId.New(), CancellationToken.None);

        Assert.Equal(EvDiscoveryCommandOutcome.Failed, execution!.Outcome);
        Assert.Null(await Slice3Support.Store(fixture, clock).GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task CrashAfterReserveBeforePublishRecoversSameVersion()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var host = Slice3Support.ReadyHost();

        // Reserva (tx1) e queda antes de publicar: staging descartado, nada publicado, v1 pendente.
        _ = await Slice3Support.ReserveAsync(
            fixture, scope, env, host, clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: false);

        var store = Slice3Support.Store(fixture, clock);
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));

        var outcome = await RunUseCaseAsync(scope, project, env, host, new MutableClock(Slice2Support.Now));

        Assert.True(outcome.Reconciled);
        Assert.Equal(1, outcome.Record.DiscoveryVersion); // recuperou a MESMA versão pelos três hashes
        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));
        Assert.NotNull(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task CrashAfterPublishBeforeFinalizeRecovers()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var host = Slice3Support.ReadyHost();

        // Reserva + publicação e queda antes de finalizar: pendente + evidência publicada.
        _ = await Slice3Support.ReserveAsync(
            fixture, scope, env, host, clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);

        var store = Slice3Support.Store(fixture, clock);
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));

        var outcome = await RunUseCaseAsync(scope, project, env, host, new MutableClock(Slice2Support.Now));

        Assert.True(outcome.Reconciled);
        Assert.Equal(1, outcome.Record.DiscoveryVersion);
        Assert.NotNull(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task PreviousVersionStaysUsableUntilNewOneFinalized()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);

        // v1 (Ready) finalizada.
        await RunUseCaseAsync(scope, project, env, Slice3Support.ReadyHost(), clock);
        var v1 = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, v1!.DiscoveryVersion);
        Assert.Equal(EvDiscoveryStatus.Ready, v1.Status);

        // v2 (Blocked — permissão negada) apenas RESERVADA: a v1 permanece corrente.
        var blockedHost = Slice3Support.ArchivePermissionDeniedHost();
        _ = await Slice3Support.ReserveAsync(
            fixture, scope, env, blockedHost, clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: false);

        var stillV1 = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, stillV1!.DiscoveryVersion); // v1 ainda corrente enquanto v2 pendente
        Assert.Equal(2, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));

        // Retry da geração bloqueada reconcilia v2 e a finaliza (v1 vira Superseded).
        var outcome = await RunUseCaseAsync(scope, project, env, blockedHost, new MutableClock(Slice2Support.Now));
        Assert.True(outcome.Reconciled);
        Assert.Equal(2, outcome.Record.DiscoveryVersion);
        var current = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(2, current!.DiscoveryVersion);
        Assert.Equal(EvDiscoveryStatus.Blocked, current.Status);
    }

    [Fact]
    public async Task StaleWorkerCannotFinalizeReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var host = Slice3Support.ReadyHost();
        await Slice3Support.Inbox(fixture, clock).EnqueueAsync(Command(scope, project, env), CancellationToken.None);
        var claimed = await Slice3Support.Inbox(fixture, clock).TryClaimNextAsync(scope, WorkerA, Slice3Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);
        var fence = new JobFence(scope, claimed!.Job.JobId, WorkerA, claimed.Job.Epoch);

        // Reserva + publicação sob o fence válido (lease vigente).
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, host, clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true, fence);
        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, env.EnvironmentId, reservation.DiscoveryVersion);

        clock.Advance(TimeSpan.FromMinutes(7)); // lease vencido: worker defasado

        var store = Slice3Support.Store(fixture, clock);
        var evidence = Slice3Support.Evidence(fixture);
        await Assert.ThrowsAsync<FencedOutException>(() => store.FinalizeAsync(
            scope, reservation, fence,
            async token =>
            {
                var content = await evidence.GetAsync(descriptor, token) ?? throw new EvDiscoveryEvidenceException("ausente");
                return new EvDiscoveryEvidenceReference(descriptor.LogicalPath, content.Bytes.Length, content.ContentSha256);
            },
            CancellationToken.None));

        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None)); // nunca promovida
    }

    [Fact]
    public async Task ConcurrentReserveOfSameEvidenceYieldsOnePendingVersion()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);

        // Dois "workers" reservam a MESMA evidência concorrentemente.
        Task<EvDiscoveryReservation> Reserve() => Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: false);

        var results = await Task.WhenAll(Reserve(), Reserve());

        Assert.Equal(results[0].DiscoveryVersion, results[1].DiscoveryVersion); // ambos obtêm a MESMA reserva
        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None)); // uma só versão pendente
        Assert.True(results[0].Reconciled ^ results[1].Reconciled); // exatamente um criou; o outro reconciliou
    }

    [Fact]
    public async Task ConcurrentReserveOfDifferentEvidenceYieldsDistinctVersions()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);

        // Evidências DIFERENTES (Ready vs. permissão negada) no mesmo ambiente ⇒ hashes distintos ⇒ versões distintas.
        Task<EvDiscoveryReservation> Reserve(FixtureEvPowerShellHost host) => Slice3Support.ReserveAsync(
            fixture, scope, env, host, clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: false);

        var results = await Task.WhenAll(Reserve(Slice3Support.ReadyHost()), Reserve(Slice3Support.ArchivePermissionDeniedHost()));

        Assert.NotEqual(results[0].DiscoveryVersion, results[1].DiscoveryVersion);
        var versions = new[] { results[0].DiscoveryVersion, results[1].DiscoveryVersion }.OrderBy(v => v).ToArray();
        Assert.Equal(1, versions[0]);
        Assert.Equal(2, versions[1]);
        Assert.Equal(2, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task DifferentConfigurationDoesNotReuseIncompatibleReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);
        var host = Slice3Support.ReadyHost();

        // Mesma evidência semântica/observada, mas HASH DE CONFIGURAÇÃO diferente (contexto de projeto distinto).
        var configA = project.ConfigurationHash;
        var configB = new Sha256Hash(new string('a', 64));

        var first = await Slice3Support.ReserveAsync(fixture, scope, env, host, clock, project.ConfigurationVersion.Value, configA, publish: false);
        var second = await Slice3Support.ReserveAsync(fixture, scope, env, host, clock, project.ConfigurationVersion.Value, configB, publish: false);

        Assert.Equal(1, first.DiscoveryVersion);
        Assert.False(first.Reconciled);
        Assert.Equal(2, second.DiscoveryVersion); // configuração incompatível ⇒ NÃO reaproveita a reserva
        Assert.False(second.Reconciled);
        Assert.NotEqual(first.ConfigurationHash.Value, second.ConfigurationHash.Value);
    }

    [Fact]
    public async Task DiscoveryIsIsolatedByTenant()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scopeA, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        await RunUseCaseAsync(scopeA, project, env, Slice3Support.ReadyHost(), clock);

        // Outro tenant não enxerga a descoberta do tenant A (RLS por SESSION_CONTEXT).
        var scopeB = SqlServerFixture.NewScope();
        Assert.Null(await Slice3Support.Store(fixture, clock).GetUsableAsync(scopeB, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedEvidenceFailsClosed()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        await RunUseCaseAsync(scope, project, env, Slice3Support.ReadyHost(), clock);

        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, env.EnvironmentId, 1);
        var finalDir = Path.Combine(fixture.ArtifactRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            env.EnvironmentId.Value.ToString("N"), "v1");
        await File.WriteAllTextAsync(Path.Combine(finalDir, "evidence.sha256"), new string('0', 64) + "\n", CancellationToken.None);

        await Assert.ThrowsAsync<EvDiscoveryEvidenceException>(() =>
            Slice3Support.Evidence(fixture).GetAsync(descriptor, CancellationToken.None));
    }

    private static EvDiscoveryRunResult DuplicateAdapterResult(EvEnvironmentDescriptor env)
    {
        var identity = new EvEnvironmentIdentity(env.EnvironmentId, env.SiteName, env.DirectoryServer, "15.1", "15.1", "PowerShell", Slice2Support.Now);
        var cap = new EvCapability(new EvCapabilityCode(EvCapabilityCodes.EvExportCmdletAvailable), 1, CapabilityAvailability.Available, "ref", null, Slice2Support.Now);
        var capabilitySet = EvCapabilitySet.Create(env.EnvironmentId, new EvAdapterId("dup"), 1, EvDiscoverySchema.Version, [cap], EvDiscoveryStatus.Ready);
        // Duas avaliações com o MESMO AdapterId (record construído direto) — inválido por invariante.
        var evalA = new EvAdapterEvaluation(new EvAdapterId("dup"), 1, AdapterCompatibility.Supported, [cap], [], [], 10, "P", null);
        var evalB = new EvAdapterEvaluation(new EvAdapterId("dup"), 1, AdapterCompatibility.Supported, [cap], [], [], 20, "P", null);
        var selection = new EvAdapterSelection(AdapterSelectionOutcome.Supported, evalA, [evalA.AdapterId, evalB.AdapterId], [evalA, evalB], []);
        return new EvDiscoveryRunResult(
            DiscoveryRunId.New(), identity, capabilitySet, selection, null, [], EvDiscoveryStatus.Ready,
            new EvDiscoveryResultCode(EvDiscoveryResultCodes.DiscoveryCompleted), Slice2Support.Now, Slice2Support.Now);
    }

    [Fact]
    public async Task ReserveRejectsDuplicateAdapterIdBeforeInsertKeepingPreviousVersion()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);

        // v1 válida e utilizável.
        await RunUseCaseAsync(scope, project, env, Slice3Support.ReadyHost(), clock);
        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));

        // A reserva de um resultado com AdapterId duplicado é recusada ANTES do INSERT (invariante de domínio,
        // não SqlException 2601/2627) — nenhuma versão parcial é persistida.
        var invalid = DuplicateAdapterResult(env);
        var dummy = new Sha256Hash(new string('0', 64));
        await Assert.ThrowsAsync<EvDiscoveryInvariantException>(() => store.ReserveAsync(
            scope, env.EnvironmentId, invalid, dummy, dummy, dummy, 1, CorrelationId.New(), fence: null, CancellationToken.None));

        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None)); // sem v2 parcial
        var usable = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, usable!.DiscoveryVersion); // v1 permanece utilizável
        Assert.Equal(EvDiscoveryStatus.Ready, usable.Status);
    }

    [Fact]
    public async Task PersistedMaturityMatchesEvaluationIncludingAutomatedFixtureValidated()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var host = Slice3Support.ReadyHost();

        // Maturidade ESPERADA = a do adapter selecionado na avaliação (assinatura reconhecida em runtime).
        var observation = await Slice3Support.Discovery(host, clock).ProbeAsync(env, Policy, CancellationToken.None);
        var selection = Slice3Support.Adapters().Select(observation, Policy);
        var expected = selection.Selected!.Maturity!;
        Assert.True(expected.OfficialDocumentation);
        Assert.True(expected.AutomatedFixtureValidated);
        Assert.False(expected.LaboratoryValidated); // nunca homologado sem laboratório

        await RunUseCaseAsync(scope, project, env, host, clock);
        var record = await Slice3Support.Store(fixture, clock).GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.NotNull(record);

        await using var connection = await fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT runtime_observed, official_documentation, automated_fixture_validated, laboratory_validated " +
            "FROM dbo.ev_adapter_evaluations WHERE environment_id = @env AND project_id = @project " +
            "AND discovery_version = @v AND adapter_id = @adapter;",
            connection.Connection);
        command.Parameters.AddWithValue("@env", env.EnvironmentId.Value);
        command.Parameters.AddWithValue("@project", scope.Project.Value);
        command.Parameters.AddWithValue("@v", record!.DiscoveryVersion);
        command.Parameters.AddWithValue("@adapter", selection.Selected!.AdapterId.Value);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        // Os QUATRO campos de maturidade persistidos correspondem ao objeto usado na avaliação.
        Assert.Equal(expected.RuntimeObserved, reader.GetBoolean(0));
        Assert.Equal(expected.OfficialDocumentation, reader.GetBoolean(1));
        Assert.Equal(expected.AutomatedFixtureValidated, reader.GetBoolean(2));
        Assert.Equal(expected.LaboratoryValidated, reader.GetBoolean(3));
    }

    [Fact]
    public async Task PublishedEvidenceRecordsAuthoritativeFingerprintsMatchingSqlReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        await RunUseCaseAsync(scope, project, env, Slice3Support.ReadyHost(), clock);

        var record = await Slice3Support.Store(fixture, clock).GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.NotNull(record);

        var finalDir = Path.Combine(fixture.ArtifactRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            env.EnvironmentId.Value.ToString("N"), "v1");
        var json = await File.ReadAllBytesAsync(Path.Combine(finalDir, "evidence.json"), CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var fingerprints = doc.RootElement.GetProperty("ReservationFingerprints");

        // As impressões autoritativas registradas no artefato batem com a reserva persistida no SQL.
        Assert.Equal(record!.ConfigurationHash.Value, fingerprints.GetProperty("ConfigurationHash").GetString());
        Assert.Equal(record.SemanticEvidenceHash.Value, fingerprints.GetProperty("SemanticEvidenceHash").GetString());
        // Avaliação de adapter completa presente no artefato — auditoria da seleção sem depender do SQL.
        Assert.NotEmpty(doc.RootElement.GetProperty("AdapterEvaluations").EnumerateArray());
    }

    // ---- Evidência publicada vs. reserva: a finalização confere caminho/tamanho/conteúdo + hashes autoritativos ----

    private async Task<EvDiscoveryEvidenceReference> PublishedReferenceAsync(TenantScope scope, EvEnvironmentDescriptor env, int version)
    {
        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, env.EnvironmentId, version);
        var content = await Slice3Support.Evidence(fixture).GetAsync(descriptor, CancellationToken.None);
        Assert.NotNull(content);
        return new EvDiscoveryEvidenceReference(descriptor.LogicalPath, content!.Bytes.Length, content.ContentSha256);
    }

    [Fact]
    public async Task FinalizeRejectsPublishedContentShaDifferentFromReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);
        var store = Slice3Support.Store(fixture, clock);

        // Bundle internamente consistente, porém com SHA de conteúdo diferente do reservado.
        var wrong = new EvDiscoveryEvidenceReference(reservation.EvidenceLogicalPath, reservation.SizeBytes, new Sha256Hash(new string('b', 64)));
        await Assert.ThrowsAsync<EvDiscoveryPersistenceException>(() => store.FinalizeAsync(
            scope, reservation, fence: null, _ => Task.FromResult(wrong), CancellationToken.None));
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None)); // não finalizou
    }

    [Fact]
    public async Task FinalizeRejectsPublishedSizeDifferentFromReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);
        var store = Slice3Support.Store(fixture, clock);

        var wrong = (await PublishedReferenceAsync(scope, env, reservation.DiscoveryVersion)) with { SizeBytes = reservation.SizeBytes + 1 };
        await Assert.ThrowsAsync<EvDiscoveryPersistenceException>(() => store.FinalizeAsync(
            scope, reservation, fence: null, _ => Task.FromResult(wrong), CancellationToken.None));
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeRejectsPublishedLogicalPathDifferentFromReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);
        var store = Slice3Support.Store(fixture, clock);

        var wrong = (await PublishedReferenceAsync(scope, env, reservation.DiscoveryVersion)) with { LogicalPath = reservation.EvidenceLogicalPath + "-tampered" };
        await Assert.ThrowsAsync<EvDiscoveryPersistenceException>(() => store.FinalizeAsync(
            scope, reservation, fence: null, _ => Task.FromResult(wrong), CancellationToken.None));
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeRejectsReservationConfigurationHashDivergentFromSql()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);
        var store = Slice3Support.Store(fixture, clock);

        // ConfigurationHash AUTORITATIVO divergente do que foi gravado no SQL para a reserva.
        var real = await PublishedReferenceAsync(scope, env, reservation.DiscoveryVersion);
        var tampered = reservation with { ConfigurationHash = new Sha256Hash(new string('c', 64)) };
        await Assert.ThrowsAsync<EvDiscoveryPersistenceException>(() => store.FinalizeAsync(
            scope, tampered, fence: null, _ => Task.FromResult(real), CancellationToken.None));
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task FinalizeRejectsReservationSemanticHashDivergentFromSql()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);
        var store = Slice3Support.Store(fixture, clock);

        var real = await PublishedReferenceAsync(scope, env, reservation.DiscoveryVersion);
        var tampered = reservation with { SemanticEvidenceHash = new Sha256Hash(new string('d', 64)) };
        await Assert.ThrowsAsync<EvDiscoveryPersistenceException>(() => store.FinalizeAsync(
            scope, tampered, fence: null, _ => Task.FromResult(real), CancellationToken.None));
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task ReconciledReservationRecoversRealSizeFromSql()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();

        var first = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: false);
        Assert.False(first.Reconciled);
        Assert.True(first.SizeBytes > 0);

        // Segunda reserva da MESMA evidência ⇒ reconciliada, recuperando o TAMANHO REAL do SQL (não zero).
        var second = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ReadyHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: false);
        Assert.True(second.Reconciled);
        Assert.Equal(first.DiscoveryVersion, second.DiscoveryVersion);
        Assert.Equal(first.SizeBytes, second.SizeBytes);
        Assert.True(second.SizeBytes > 0);
    }

    [Fact]
    public async Task FinalizeFailureDoesNotSupersedePreviousUsableVersion()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, project) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);

        // v1 (Ready) finalizada e utilizável.
        await RunUseCaseAsync(scope, project, env, Slice3Support.ReadyHost(), clock);
        var v1 = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, v1!.DiscoveryVersion);

        // v2 reservada+publicada; a finalização com conteúdo divergente falha e NÃO substitui a v1.
        var reservation = await Slice3Support.ReserveAsync(
            fixture, scope, env, Slice3Support.ArchivePermissionDeniedHost(), clock, project.ConfigurationVersion.Value, project.ConfigurationHash, publish: true);
        Assert.Equal(2, reservation.DiscoveryVersion);
        var wrong = new EvDiscoveryEvidenceReference(reservation.EvidenceLogicalPath, reservation.SizeBytes, new Sha256Hash(new string('e', 64)));
        await Assert.ThrowsAsync<EvDiscoveryPersistenceException>(() => store.FinalizeAsync(
            scope, reservation, fence: null, _ => Task.FromResult(wrong), CancellationToken.None));

        var still = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, still!.DiscoveryVersion); // v1 permanece corrente (não substituída)
        Assert.Equal(EvDiscoveryStatus.Ready, still.Status);
    }
}
