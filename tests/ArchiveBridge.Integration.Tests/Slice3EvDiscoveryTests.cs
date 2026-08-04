using ArchiveBridge.Application.EnterpriseVault.Discovery;
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
/// cercada→conclusão; contexto obsoleto bloqueado; reconciliação de quedas; versão anterior utilizável até
/// a nova finalizar; worker defasado não persiste; isolamento por tenant; evidência imutável.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice3EvDiscoveryTests(SqlServerFixture fixture)
{
    private static readonly WorkerId WorkerA = new("worker-A");
    private static readonly EvDiscoveryPolicy Policy = EvDiscoveryPolicy.Default;

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
        Assert.Equal("ev-export-adapter-modern", usable.SelectedAdapter!.Value.Value);
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
        var (scope, _) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var host = Slice3Support.ReadyHost();

        var observation = await Slice3Support.Discovery(host, clock).ProbeAsync(env, Policy, CancellationToken.None);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Slice3Support.Adapters().Select(observation, Policy), Policy, clock.UtcNow, clock.UtcNow);
        var bytes = new Infrastructure.EnterpriseVault.Discovery.EvDiscoveryEvidenceSerializer().Serialize(result);
        var evidence = Slice3Support.Evidence(fixture);
        var staging = await evidence.StageAsync(bytes.Bytes, bytes.ContentSha256, CancellationToken.None);
        _ = await Slice3Support.Store(fixture, clock).ReserveAsync(scope, env.EnvironmentId, result, staging.SizeBytes, CorrelationId.New(), fence: null, CancellationToken.None);
        await evidence.DiscardAsync(staging, CancellationToken.None); // queda: staging perdido, nada publicado

        var store = Slice3Support.Store(fixture, clock);
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));

        var outcome = await Slice3Support.UseCase(fixture, host, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, env, Policy, CorrelationId.New(), CancellationToken.None);

        Assert.True(outcome.Reconciled);
        Assert.Equal(1, outcome.Record.DiscoveryVersion); // recuperou a MESMA versão
        Assert.Equal(1, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));
        Assert.NotNull(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task CrashAfterPublishBeforeFinalizeRecovers()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, _) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var host = Slice3Support.ReadyHost();

        var observation = await Slice3Support.Discovery(host, clock).ProbeAsync(env, Policy, CancellationToken.None);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Slice3Support.Adapters().Select(observation, Policy), Policy, clock.UtcNow, clock.UtcNow);
        var bytes = new Infrastructure.EnterpriseVault.Discovery.EvDiscoveryEvidenceSerializer().Serialize(result);
        var evidence = Slice3Support.Evidence(fixture);
        var staging = await evidence.StageAsync(bytes.Bytes, bytes.ContentSha256, CancellationToken.None);
        var reservation = await Slice3Support.Store(fixture, clock).ReserveAsync(scope, env.EnvironmentId, result, staging.SizeBytes, CorrelationId.New(), fence: null, CancellationToken.None);
        await evidence.PublishAsync(staging, new EvDiscoveryEvidenceDescriptor(scope, env.EnvironmentId, reservation.DiscoveryVersion), CancellationToken.None);
        // queda antes de finalizar: pendente + evidência publicada

        var store = Slice3Support.Store(fixture, clock);
        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));

        var outcome = await Slice3Support.UseCase(fixture, host, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, env, Policy, CorrelationId.New(), CancellationToken.None);

        Assert.True(outcome.Reconciled);
        Assert.Equal(1, outcome.Record.DiscoveryVersion);
        Assert.NotNull(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task PreviousVersionStaysUsableUntilNewOneFinalized()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, _) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        var store = Slice3Support.Store(fixture, clock);

        // v1 (Ready) finalizada.
        await Slice3Support.UseCase(fixture, Slice3Support.ReadyHost(), clock).ExecuteAsync(scope, env, Policy, CorrelationId.New(), CancellationToken.None);
        var v1 = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, v1!.DiscoveryVersion);

        // v2 (Blocked — permissões ausentes) apenas RESERVADA: a v1 permanece corrente.
        var blockedHost = new FixtureEvPowerShellHost(Slice3Support.BlockedSnapshotJson, Slice3Support.ModernCommandJson);
        var observation = await Slice3Support.Discovery(blockedHost, clock).ProbeAsync(env, Policy, CancellationToken.None);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Slice3Support.Adapters().Select(observation, Policy), Policy, clock.UtcNow, clock.UtcNow);
        var bytes = new Infrastructure.EnterpriseVault.Discovery.EvDiscoveryEvidenceSerializer().Serialize(result);
        var evidence = Slice3Support.Evidence(fixture);
        var staging = await evidence.StageAsync(bytes.Bytes, bytes.ContentSha256, CancellationToken.None);
        _ = await store.ReserveAsync(scope, env.EnvironmentId, result, staging.SizeBytes, CorrelationId.New(), fence: null, CancellationToken.None);

        var stillV1 = await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None);
        Assert.Equal(1, stillV1!.DiscoveryVersion); // v1 ainda corrente enquanto v2 pendente
        Assert.Equal(2, await store.GetMaxVersionAsync(scope, env.EnvironmentId, CancellationToken.None));
        await evidence.DiscardAsync(staging, CancellationToken.None);

        // Retry da geração bloqueada reconcilia v2 e a finaliza (v1 vira Superseded).
        var outcome = await Slice3Support.UseCase(fixture, blockedHost, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, env, Policy, CorrelationId.New(), CancellationToken.None);
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

        var observation = await Slice3Support.Discovery(host, clock).ProbeAsync(env, Policy, CancellationToken.None);
        var result = EvDiscoveryEvaluator.Evaluate(DiscoveryRunId.New(), observation, Slice3Support.Adapters().Select(observation, Policy), Policy, clock.UtcNow, clock.UtcNow);
        var bytes = new Infrastructure.EnterpriseVault.Discovery.EvDiscoveryEvidenceSerializer().Serialize(result);
        var evidence = Slice3Support.Evidence(fixture);
        var store = Slice3Support.Store(fixture, clock);
        var staging = await evidence.StageAsync(bytes.Bytes, bytes.ContentSha256, CancellationToken.None);
        var reservation = await store.ReserveAsync(scope, env.EnvironmentId, result, staging.SizeBytes, CorrelationId.New(), fence, CancellationToken.None);
        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, env.EnvironmentId, reservation.DiscoveryVersion);
        await evidence.PublishAsync(staging, descriptor, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(7)); // lease vencido: worker defasado

        await Assert.ThrowsAsync<FencedOutException>(() => store.FinalizeAsync(
            scope, reservation, fence,
            async token => _ = await evidence.GetAsync(descriptor, token) ?? throw new EvDiscoveryEvidenceException("ausente"),
            CancellationToken.None));

        Assert.Null(await store.GetUsableAsync(scope, env.EnvironmentId, CancellationToken.None)); // nunca promovida
    }

    [Fact]
    public async Task DiscoveryIsIsolatedByTenant()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scopeA, _) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        await Slice3Support.UseCase(fixture, Slice3Support.ReadyHost(), clock).ExecuteAsync(scopeA, env, Policy, CorrelationId.New(), CancellationToken.None);

        // Outro tenant não enxerga a descoberta do tenant A (RLS por SESSION_CONTEXT).
        var scopeB = SqlServerFixture.NewScope();
        Assert.Null(await Slice3Support.Store(fixture, clock).GetUsableAsync(scopeB, env.EnvironmentId, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedEvidenceFailsClosed()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, _) = await SeedProjectAsync();
        var env = Slice3Support.NewEnvironment();
        await Slice3Support.UseCase(fixture, Slice3Support.ReadyHost(), clock).ExecuteAsync(scope, env, Policy, CorrelationId.New(), CancellationToken.None);

        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, env.EnvironmentId, 1);
        var finalDir = Path.Combine(fixture.ArtifactRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            env.EnvironmentId.Value.ToString("N"), "v1");
        await File.WriteAllTextAsync(Path.Combine(finalDir, "evidence.sha256"), new string('0', 64) + "\n", CancellationToken.None);

        await Assert.ThrowsAsync<EvDiscoveryEvidenceException>(() =>
            Slice3Support.Evidence(fixture).GetAsync(descriptor, CancellationToken.None));
    }
}
