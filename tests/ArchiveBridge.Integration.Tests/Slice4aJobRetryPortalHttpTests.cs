using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A Passo 7 — retry manual autorizado de Jobs em nível HTTP real, ligado ao SQL Server real. Prova
/// RBAC server-side, precedência RBAC-antes-do-gate, antiforgery, anti-IDOR, elegibilidade fail-closed
/// (nunca ressuscita estado terminal), idempotência/replay, conflito de chave, concorrência e auditoria —
/// SEM iniciar Passo 8/Slice 4B e sem qualquer UPDATE direto de estado pela camada web.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aJobRetryPortalHttpTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private readonly SqlServerFixture _fixture = fixture;

    // ---- RBAC: apenas Operator/Administrator podem solicitar (POST HTTP real, não visibilidade de botão).

    [Fact]
    public async Task OperatorRequestOnEligibleJobIsAcceptedAndExpeditesNextAttempt()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // 302 (PRG)
        Assert.Contains("/Jobs/Details", response.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var snapshot = await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(JobState.RetryScheduled, snapshot!.State); // nunca uma transição de estado
        Assert.True(snapshot.NextAttemptAtUtc <= clock.UtcNow);

        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && e.ActionCode == "jobs.retry.request" && e.Succeeded && e.Reason == "accepted");
    }

    [Fact]
    public async Task AdministratorRequestIsAccepted()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Administrator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData(PortalRoles.Viewer)]
    [InlineData(PortalRoles.Auditor)]
    [InlineData(PortalRoles.Approver)]
    public async Task NonOperatorRolesAreForbiddenAndApplyNoRetry(string role)
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, role);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        var before = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // 403 real
        var after = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        Assert.Equal(before, after); // zero mutação
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "forbidden");
    }

    // ---- Identidade fail-closed: autenticado, porém sem UserId utilizável ⇒ 403 e zero efeito.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task AuthenticatedPrincipalWithoutUsableUserIdIsForbiddenWithZeroEffects(string? userIdClaim)
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var username = "stub_" + Guid.NewGuid().ToString("N");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(PortalClaims.TenantId, scope.Tenant.Value.ToString()),
            new(PortalClaims.ProjectId, scope.Project.Value.ToString()),
            new(ClaimTypes.Role, PortalRoles.Operator),
        };
        if (userIdClaim is not null)
        {
            claims.Add(new Claim(PortalClaims.UserId, userIdClaim));
        }

        using var factory = CreateFactory(retryEnabled: true, stubPrincipalClaims: claims);
        using var client = factory.CreateClient(NoRedirect());

        var before = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        Assert.Equal(before, after);
        Assert.DoesNotContain(await AuditAsync(scope), e => e.ActionCode == "jobs.retry.request"); // recusa precede a auditoria de negócio
    }

    // ---- Anônimo: challenge/login, zero mutação.

    [Fact]
    public async Task AnonymousRequestRedirectsToLoginAndAppliesNoRetry()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());

        var before = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        using var response = await client.PostAsync(
            new Uri("/Jobs/Details?handler=Retry", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("jobId", jobId.Value.ToString("D")),
                new KeyValuePair<string, string>("idempotencyKey", Guid.NewGuid().ToString("D")),
            ]));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var after = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        Assert.Equal(before, after);
    }

    // ---- Feature gate.

    [Fact]
    public async Task FeatureDisabledReturns503AndAppliesNoRetry()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: false);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var after = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        Assert.NotEqual(clock.UtcNow, after);
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "feature-disabled");
    }

    // ---- Precedência: RBAC é avaliado ANTES do feature gate.

    [Fact]
    public async Task NonOperatorIsForbiddenEvenWhenGateDisabled()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        using var factory = CreateFactory(retryEnabled: false); // gate DESABILITADO
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // NÃO 503 — RBAC precede o gate
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "forbidden");
        Assert.DoesNotContain(events, e => e.Username == username && e.Reason == "feature-disabled");
    }

    // ---- Antiforgery.

    [Fact]
    public async Task PostWithoutAntiforgeryTokenIsRejectedAndAppliesNoRetry()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("jobId", jobId.Value.ToString("D")),
            new KeyValuePair<string, string>("idempotencyKey", Guid.NewGuid().ToString("D")),
        ]);
        using var response = await client.PostAsync(new Uri("/Jobs/Details?handler=Retry", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = (await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None))!.NextAttemptAtUtc;
        Assert.NotEqual(clock.UtcNow, after);
    }

    // ---- Anti-IDOR.

    [Fact]
    public async Task NonExistentJobReturns404()
    {
        var scope = SqlServerFixture.NewScope();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "not-found-or-not-authorized");
    }

    [Fact]
    public async Task JobInAnotherProjectSameTenantReturns404()
    {
        var clock = new MutableClock(Start);
        var operatorScope = SqlServerFixture.NewScope();
        var otherProjectScope = new TenantScope(operatorScope.Tenant, new ProjectId(Guid.NewGuid()));
        var foreignJobId = await ScheduleRetryAsync(clock, otherProjectScope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(operatorScope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, foreignJobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var snapshot = await _fixture.Store(clock).GetAsync(otherProjectScope, foreignJobId, CancellationToken.None);
        Assert.NotEqual(clock.UtcNow, snapshot!.NextAttemptAtUtc);
    }

    [Fact]
    public async Task JobInAnotherTenantReturns404()
    {
        var clock = new MutableClock(Start);
        var operatorScope = SqlServerFixture.NewScope();
        var otherTenantScope = SqlServerFixture.NewScope();
        var foreignJobId = await ScheduleRetryAsync(clock, otherTenantScope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(operatorScope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, foreignJobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // externamente idêntico ao cross-project
    }

    // ---- Elegibilidade fail-closed: nunca ressuscita um estado não-RetryScheduled.

    [Theory]
    [InlineData("Pending")]
    [InlineData("Processing")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public async Task JobInNonEligibleStateReturns409AndAppliesNoTransition(string state)
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await SeedJobInStateAsync(clock, scope, Enum.Parse<JobState>(state));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var snapshot = await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(Enum.Parse<JobState>(state), snapshot!.State); // sem transição indevida
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "precondition-failed");
    }

    // ---- Idempotência HTTP.

    [Fact]
    public async Task SameIdempotencyKeyReplaysWithoutDuplicatingTheEffect()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var first = await PostRetryAsync(client, jobId.Value, idempotencyKey);
        using var second = await PostRetryAsync(client, jobId.Value, idempotencyKey);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);

        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && e.Succeeded && e.Reason == "accepted");
        Assert.Contains(events, e => e.Username == username && e.Succeeded && e.Reason == "idempotent-replay");

        var transitions = await _fixture.AuditReader().GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, t => t.Reason == ReasonCode.RetryExpedited); // um único efeito lógico
    }

    // ---- Conflito de idempotência (mesma key, Job diferente) ⇒ 409, sem efeito no segundo Job.

    [Fact]
    public async Task SameKeyForADifferentJobReturns409Conflict()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobA = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var jobB = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var first = await PostRetryAsync(client, jobA.Value, idempotencyKey);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        using var second = await PostRetryAsync(client, jobB.Value, idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var snapshotB = await _fixture.Store(clock).GetAsync(scope, jobB, CancellationToken.None);
        Assert.NotEqual(clock.UtcNow, snapshotB!.NextAttemptAtUtc);
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "idempotency-conflict");
    }

    // ---- Validação de input.

    [Fact]
    public async Task EmptyIdempotencyKeyReturns400AndAppliesNoRetry()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Presentation Mode: zero acesso/mutação real.

    [Fact]
    public async Task PresentationModeRefusesRetryAndAppliesNothing()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(retryEnabled: true, presentation: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRetryAsync(client, jobId.Value, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var snapshot = await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None);
        Assert.NotEqual(clock.UtcNow, snapshot!.NextAttemptAtUtc);
        Assert.Empty(await AuditAsync(scope)); // demonstração é estritamente somente leitura
    }

    // ---- Falha de auditoria após efeito aplicado: 500 fail-closed; efeito persiste; retry idempotente.

    [Fact]
    public async Task AuditFailureAfterEffectReturns500AndRetryIsIdempotent()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        var jobId = await ScheduleRetryAsync(clock, scope, TimeSpan.FromHours(1));
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        using (var failingFactory = CreateFactory(
            retryEnabled: true,
            configureServices: services =>
            {
                services.RemoveAll<IPortalOperationalAudit>();
                services.AddScoped<IPortalOperationalAudit>(_ => new FailingOperationalAudit());
            }))
        using (var client = failingFactory.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var response = await PostRetryAsync(client, jobId.Value, idempotencyKey);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        var afterFailure = await _fixture.Store(clock).GetAsync(scope, jobId, CancellationToken.None);
        Assert.Equal(clock.UtcNow, afterFailure!.NextAttemptAtUtc); // o efeito durável já foi aplicado

        using (var okFactory = CreateFactory(retryEnabled: true))
        using (var client = okFactory.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var retry = await PostRetryAsync(client, jobId.Value, idempotencyKey);
            Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode); // replay idempotente, não duplica
        }

        var transitions = await _fixture.AuditReader().GetTransitionsAsync(scope, jobId, CancellationToken.None);
        Assert.Single(transitions, t => t.Reason == ReasonCode.RetryExpedited);
    }

    // ============================ infra de teste ============================

    private WebApplicationFactory<Program> CreateFactory(
        bool retryEnabled,
        bool presentation = false,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyList<Claim>? stubPrincipalClaims = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (stubPrincipalClaims is not null)
            {
                // Só para exercitar principais que o login normal NUNCA emite (identidade ausente/inválida).
                builder.ConfigureTestServices(services => services
                    .AddAuthentication(StubPrincipalHandler.SchemeName)
                    .AddScheme<StubPrincipalOptions, StubPrincipalHandler>(
                        StubPrincipalHandler.SchemeName, options => options.Claims = stubPrincipalClaims));
            }

            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", _fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", _fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", _fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
            builder.UseSetting("JobRetry:Enabled", retryEnabled ? "true" : "false");
            builder.UseSetting("PresentationMode:Enabled", presentation ? "true" : "false");
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });

    private static WebApplicationFactoryClientOptions NoRedirect() => new() { AllowAutoRedirect = false };

    private async Task<(string Username, string Password)> SeedUserAsync(TenantScope scope, string role)
    {
        var username = "u_" + Guid.NewGuid().ToString("N");
        const string password = "Str0ng!pw";
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Stage7 " + role, scope.Tenant, scope.Project, Hasher.Hash(password), [role]),
            CancellationToken.None);
        return (username, password);
    }

    /// <summary>Cria um Job e o leva a RetryScheduled (Pending → Processing → RetryScheduled) via a fila durável real.</summary>
    private async Task<JobId> ScheduleRetryAsync(MutableClock clock, TenantScope scope, TimeSpan nextAttemptOffset)
    {
        var store = _fixture.Store(clock);
        var worker = new WorkerId("worker-" + Guid.NewGuid().ToString("N")[..8]);
        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());
        await store.ScheduleRetryAsync(
            lease, ErrorCode.TransientProvider, clock.UtcNow + nextAttemptOffset, CancellationToken.None);
        return jobId;
    }

    private async Task<JobId> SeedJobInStateAsync(MutableClock clock, TenantScope scope, JobState targetState)
    {
        var store = _fixture.Store(clock);
        var worker = new WorkerId("worker-" + Guid.NewGuid().ToString("N")[..8]);
        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.Pst, JobPriority.Normal, CorrelationId.New()),
            CancellationToken.None);

        if (targetState == JobState.Pending)
        {
            return jobId;
        }

        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Pst, worker, Lease, CorrelationId.New()),
            CancellationToken.None);
        var lease = new LeaseCommand(scope, jobId, worker, claimed!.Epoch, CorrelationId.New());

        switch (targetState)
        {
            case JobState.Processing:
                break;
            case JobState.Completed:
                await store.CompleteAsync(lease, CancellationToken.None);
                break;
            case JobState.Failed:
                await store.FailAsync(lease, ErrorCode.PermanentProvider, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetState), targetState, "Estado não coberto pelo seed de teste.");
        }

        return jobId;
    }

    private Task<IReadOnlyList<PortalOperationalAuditEvent>> AuditAsync(TenantScope scope) =>
        new SqlPortalOperationalAudit(_fixture.Factory).RecentAsync(scope, 100, CancellationToken.None);

    private static async Task<HttpResponseMessage> PostRetryAsync(HttpClient client, Guid jobId, Guid idempotencyKey)
    {
        // O token antiforgery é obtido via /Account/Login (SEMPRE renderiza um formulário, para qualquer
        // papel/gate/estado do job) — o par cookie+campo do antiforgery padrão do ASP.NET Core não é
        // vinculado a uma página/rota específica, apenas à sessão autenticada corrente no HttpClient.
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("jobId", jobId.ToString("D")),
            new KeyValuePair<string, string>("idempotencyKey", idempotencyKey.ToString("D")),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]);
        return await client.PostAsync(new Uri("/Jobs/Details?handler=Retry", UriKind.Relative), content);
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Username", username),
            new KeyValuePair<string, string>("Password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]);
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Token antiforgery não encontrado em {path}.");
        return match.Groups[1].Value;
    }

    private sealed class FailingOperationalAudit : IPortalOperationalAudit
    {
        public Task RecordAsync(TenantScope scope, PortalOperationalAuditEvent auditEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha deliberada de auditoria operacional (teste).");

        public Task<IReadOnlyList<PortalOperationalAuditEvent>> RecentAsync(TenantScope scope, int max, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha deliberada de auditoria operacional (teste).");

        public Task<KeysetPage<PortalOperationalAuditEvent, AuditSeekPosition>> SearchAsync(
            TenantScope scope, PortalOperationalAuditFilter filter, int pageSize, AuditSeekPosition? after, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha deliberada de auditoria operacional (teste).");
    }
}
