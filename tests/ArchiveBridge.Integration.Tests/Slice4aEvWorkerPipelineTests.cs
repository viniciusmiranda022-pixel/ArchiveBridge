using System.Data;
using System.Globalization;
using System.Text.Json;
using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A (sub-incremento 2) — o consumidor operacional durável ponta a ponta contra SQL Server real,
/// SEM Enterprise Vault real: solicitação → fila durável → leitor de escopos (identidade de manutenção,
/// somente enumeração) → processor (identidade da aplicação) → descoberta READ-ONLY (fake de
/// <see cref="IEvCapabilityDiscovery"/>) → Job terminal → run persistido → evidência publicada/relível.
/// Também prova o filtro do leitor de escopos e que a enumeração de manutenção NÃO é autorização.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aEvWorkerPipelineTests(SqlServerFixture fixture)
{
    private static readonly WorkerId Worker = new("ev-worker-e2e");
    private static readonly EvDiscoveryPolicy Policy = EvDiscoveryPolicy.Default;
    private readonly SqlServerFixture _fixture = fixture;

    [Fact]
    public async Task DiscoveryRequestFlowsDurablyToEvidenceWithoutRealEv()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var scope = SqlServerFixture.NewScope();
        var project = Slice2Support.NewProject(scope);
        await Slice2Support.ProjectStore(_fixture, clock).AddAsync(project, CorrelationId.New(), CancellationToken.None);
        var environmentId = new EvEnvironmentId(Guid.NewGuid());
        await SeedEnvironmentAsync(scope, environmentId, "site-e2e", "dir-e2e.contoso.local");

        // 1. SOLICITAÇÃO: contexto (site/directory, versão/hash de config, política) resolvido server-side e
        //    enfileiramento durável idempotente — o cliente não fornece nenhum desses valores.
        var request = new RequestEvCapabilityDiscoveryUseCase(
            new SqlEvEnvironmentCatalog(_fixture.Factory),
            Slice2Support.ProjectStore(_fixture, clock),
            new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock),
            Policy);
        var requested = await request.ExecuteAsync(
            new RequestEvCapabilityDiscovery(scope, environmentId, "operator@contoso.local", Guid.NewGuid(), CorrelationId.New()),
            CancellationToken.None);
        Assert.True(requested.Created);

        // 2. O leitor de escopos (identidade de manutenção, read-only) encontra o escopo elegível.
        var scopeReader = new SqlEvDiscoveryPendingScopeReader(_fixture.Factory, clock);
        Assert.Contains(scope, await scopeReader.ListEligibleScopesAsync(100_000, CancellationToken.None));

        // 3. O processor processa com um FAKE de IEvCapabilityDiscovery (sem EV real); o restante é REAL.
        var execution = await BuildProcessor(clock).ProcessNextAsync(
            scope, Worker, Slice3Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(execution);
        Assert.Equal(EvDiscoveryCommandOutcome.Completed, execution!.Outcome);
        Assert.Equal(requested.JobId, execution.Job);

        // 4. Job terminal (Completed).
        var job = await _fixture.Store(clock).GetAsync(scope, requested.JobId, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(JobState.Completed, job!.State);

        // 5. ev_discovery_run persistido e utilizável.
        var usable = await Slice3Support.Store(_fixture, clock).GetUsableAsync(scope, environmentId, CancellationToken.None);
        Assert.NotNull(usable);
        Assert.Equal(EvDiscoveryStatus.Ready, usable!.Status);

        // 6. evidence.json publicado; GetAsync valida sidecar SHA-256/manifesto/escopo (bundle RELÍVEL).
        var descriptor = new EvDiscoveryEvidenceDescriptor(scope, environmentId, usable.DiscoveryVersion);
        var evidence = Slice3Support.Evidence(_fixture);
        var content = await evidence.GetAsync(descriptor, CancellationToken.None);
        Assert.NotNull(content);
        Assert.Equal(usable.ContentSha256, content!.ContentSha256); // hash de conteúdo confere com o SQL
        using (var doc = JsonDocument.Parse(content.Bytes))          // evidence.json é JSON válido e reparseável
        {
            Assert.Equal(
                usable.SemanticEvidenceHash.Value,
                doc.RootElement.GetProperty("ReservationFingerprints").GetProperty("SemanticEvidenceHash").GetString());
        }

        // Bundle pode ser relido novamente (idempotente; mesmos bytes/hash).
        var reread = await evidence.GetAsync(descriptor, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal(content.ContentSha256, reread!.ContentSha256);
    }

    [Fact]
    public async Task PendingScopeReaderListsOnlyEligibleEvScopes()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var reader = new SqlEvDiscoveryPendingScopeReader(_fixture.Factory, clock);

        // Tenant A / Project 1: Job EV Pending elegível.
        var a1 = SqlServerFixture.NewScope();
        await EnqueueEvAsync(a1, clock);

        // Tenant A / Project 2: mesmo tenant, sem Job.
        var a2 = new TenantScope(a1.Tenant, new ProjectId(Guid.NewGuid()));

        // Tenant B / Project 3: Job EV RetryScheduled no FUTURO (ainda não elegível).
        var b3 = SqlServerFixture.NewScope();
        await EnqueueEvAsync(b3, clock);
        await SetJobRetryScheduledAsync(b3, clock.UtcNow.AddHours(1));

        // Tenant C / Project 4: Job de OUTRO workload (sem comando EV).
        var c4 = SqlServerFixture.NewScope();
        await _fixture.Store(clock).CreateAsync(
            new CreateJobCommand(c4, Workload.Pst, JobPriority.Normal, CorrelationId.New()), CancellationToken.None);

        var scopes = await reader.ListEligibleScopesAsync(100_000, CancellationToken.None);

        Assert.Contains(a1, scopes);       // elegível
        Assert.DoesNotContain(a2, scopes); // sem Job
        Assert.DoesNotContain(b3, scopes); // retry futuro
        Assert.DoesNotContain(c4, scopes); // outro workload
    }

    [Fact]
    public async Task RetryScheduledIsListedOnlyWhenNextAttemptElapsed()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var reader = new SqlEvDiscoveryPendingScopeReader(_fixture.Factory, clock);

        var future = SqlServerFixture.NewScope();
        await EnqueueEvAsync(future, clock);
        await SetJobRetryScheduledAsync(future, clock.UtcNow.AddHours(1));
        Assert.DoesNotContain(future, await reader.ListEligibleScopesAsync(100_000, CancellationToken.None));

        var elapsed = SqlServerFixture.NewScope();
        await EnqueueEvAsync(elapsed, clock);
        await SetJobRetryScheduledAsync(elapsed, clock.UtcNow.AddHours(-1)); // vencido
        Assert.Contains(elapsed, await reader.ListEligibleScopesAsync(100_000, CancellationToken.None));
    }

    [Fact]
    public async Task MaintenanceEnumerationIsNotAuthorizationForCrossScopeClaim()
    {
        var clock = new MutableClock(Slice2Support.Now);

        // Dois escopos de TENANTS diferentes, cada um com um comando EV elegível.
        var scopeA = SqlServerFixture.NewScope();
        var scopeB = SqlServerFixture.NewScope();
        await EnqueueEvAsync(scopeA, clock);
        await EnqueueEvAsync(scopeB, clock);

        // A enumeração de manutenção enxerga AMBOS (é só descoberta, não autorização).
        var reader = new SqlEvDiscoveryPendingScopeReader(_fixture.Factory, clock);
        var scopes = await reader.ListEligibleScopesAsync(100_000, CancellationToken.None);
        Assert.Contains(scopeA, scopes);
        Assert.Contains(scopeB, scopes);

        // Sob a identidade da aplicação de A, o claim é CONFINADO a A (filtro de projeto + RLS): reivindica
        // o comando de A, NUNCA o de B.
        var inbox = new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock);
        var claimedA = await inbox.TryClaimNextAsync(scopeA, Worker, Slice3Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimedA);
        Assert.Equal(scopeA.Project, claimedA!.Command.Scope.Project);

        // O Job de B permanece Pending — o processamento de A não o alcança.
        Assert.Equal(1, await CountPendingEvJobsAsync(scopeB));

        // RLS: a identidade de A não enxerga sequer os comandos de B (cross-tenant bloqueado).
        Assert.Equal(0, await CountCommandsForProjectUnderScopeAsync(scopeA, scopeB.Project));
    }

    private EvDiscoveryCommandProcessor BuildProcessor(MutableClock clock)
    {
        var useCase = new DiscoverEvCapabilitiesUseCase(
            new FakeReadyEvCapabilityDiscovery(clock.UtcNow),
            Slice3Support.Adapters(),
            new EvDiscoveryEvidenceSerializer(),
            Slice3Support.Store(_fixture, clock),
            Slice3Support.Evidence(_fixture),
            clock);
        return new EvDiscoveryCommandProcessor(
            Slice3Support.Inbox(_fixture, clock),
            _fixture.Store(clock),
            _fixture.LeaseManager(clock, RetryPolicy.Default, Slice3Support.Lease),
            Slice2Support.ProjectStore(_fixture, clock),
            useCase,
            Policy,
            clock,
            RetryPolicy.Default);
    }

    private static EvDiscoveryCommand BuildCommand(TenantScope scope) =>
        new(
            scope,
            new EvEnvironmentId(Guid.NewGuid()),
            "site",
            "dir.contoso.local",
            "operator@contoso.local",
            CorrelationId.New(),
            new EvDiscoveryCommandContext(
                EvDiscoveryCommandContext.CurrentSchemaVersion, 1, new Sha256Hash(new string('a', 64)), Policy.PolicyVersion));

    private async Task EnqueueEvAsync(TenantScope scope, MutableClock clock) =>
        await new SqlEvDiscoveryCommandInbox(_fixture.Factory, clock).EnqueueAsync(BuildCommand(scope), CancellationToken.None);

    private Task SeedEnvironmentAsync(TenantScope scope, EvEnvironmentId environmentId, string site, string dir) => ExecAsync(scope,
        """
        INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
        VALUES (@env, @tenant, @project, @site, @dir);
        """,
        command =>
        {
            command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = site });
            command.Parameters.Add(new SqlParameter("@dir", SqlDbType.NVarChar, 260) { Value = dir });
        });

    private Task SetJobRetryScheduledAsync(TenantScope scope, DateTimeOffset nextAttempt) => ExecAsync(scope,
        "UPDATE dbo.jobs SET state = 2, next_attempt_at_utc = @next WHERE tenant_id = @tenant AND project_id = @project AND workload = 1;",
        command =>
        {
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@next", SqlDbType.DateTime2) { Value = nextAttempt.UtcDateTime });
        });

    private async Task<int> CountPendingEvJobsAsync(TenantScope scope) =>
        await ScalarAsync(scope, "SELECT COUNT(*) FROM dbo.jobs WHERE project_id = @project AND workload = 1 AND state = 0;", _ => { });

    private async Task<int> CountCommandsForProjectUnderScopeAsync(TenantScope connectAs, ProjectId targetProject)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(connectAs, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.ev_discovery_commands WHERE project_id = @project;", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = targetProject.Value });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<int> ScalarAsync(TenantScope scope, string sql, Action<SqlCommand> bind)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        bind(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task ExecAsync(TenantScope scope, string sql, Action<SqlCommand> bind)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(sql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        bind(command);
        await command.ExecuteNonQueryAsync();
    }
}
