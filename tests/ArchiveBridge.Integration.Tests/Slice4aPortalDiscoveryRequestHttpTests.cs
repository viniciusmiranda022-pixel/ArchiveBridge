using System.Data;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A — Stage 4: a PRIMEIRA ação de escrita do Portal (solicitar descoberta EV READ-ONLY) em nível
/// HTTP real, ligada ao SQL Server real. Prova RBAC server-side, feature gate, antiforgery, anti-IDOR,
/// idempotência/replay, conflito, autoridade server-side do contexto do comando, PRG para o Job e o
/// comportamento fail-closed da auditoria — SEM subir o Worker EV e SEM executar descoberta.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aPortalDiscoveryRequestHttpTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private readonly SqlServerFixture _fixture = fixture;

    // ---- §25 RBAC: apenas Operator/Administrator podem solicitar (POST HTTP real, não visibilidade de botão).

    [Fact]
    public async Task OperatorRequestIsAcceptedAndRedirectsToJobDetails()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-op", "dir-op.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, environmentId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // 302 (PRG)
        Assert.Contains("/Jobs/Details", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await CountCommandsAsync(scope));

        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && e.ActionCode == "ev.discovery.request" && e.Succeeded && e.Reason == "accepted");
    }

    [Fact]
    public async Task AdministratorRequestIsAccepted()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-adm", "dir-adm.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Administrator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, environmentId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, await CountCommandsAsync(scope));
    }

    [Theory]
    [InlineData(PortalRoles.Viewer)]
    [InlineData(PortalRoles.Auditor)]
    [InlineData(PortalRoles.Approver)]
    public async Task NonOperatorRolesAreForbiddenAndCreateNoJob(string role)
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-deny", "dir-deny.contoso.local");
        var (username, password) = await SeedUserAsync(scope, role);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, environmentId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // 403 real (não redirect de AccessDenied)
        Assert.Equal(0, await CountCommandsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "forbidden");
    }

    // ---- §26 feature gate.

    [Fact]
    public async Task FeatureDisabledReturns503AndCreatesNoJob()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-gate", "dir-gate.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: false);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        // GET: nenhum formulário operacional é oferecido quando o gate está desabilitado.
        using (var page = await client.GetAsync(new Uri("/EnterpriseVault/Index", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("indisponível neste deployment", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // POST manual mesmo autorizado: fail-closed 503.
        using var response = await PostRequestDiscoveryAsync(client, environmentId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // 503
        Assert.Equal(0, await CountCommandsAsync(scope));
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "feature-disabled");
    }

    // ---- §27 antiforgery.

    [Fact]
    public async Task PostWithoutAntiforgeryTokenIsRejectedAndCreatesNoJob()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-csrf", "dir-csrf.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        // POST SEM o campo __RequestVerificationToken (mas autenticado): rejeitado pela antiforgery, 400.
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("EnvironmentId", environmentId.ToString("D")),
            new KeyValuePair<string, string>("IdempotencyKey", Guid.NewGuid().ToString("D")),
        ]);
        using var response = await client.PostAsync(
            new Uri("/EnterpriseVault/Index?handler=RequestDiscovery", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountCommandsAsync(scope));
    }

    // ---- §28 anti-IDOR (mesmo tenant/outro projeto e outro tenant ⇒ 404 indistinguível).

    [Fact]
    public async Task RequestForEnvironmentInAnotherProjectSameTenantReturns404()
    {
        var operatorScope = SqlServerFixture.NewScope();
        var otherProjectScope = new TenantScope(operatorScope.Tenant, new ProjectId(Guid.NewGuid()));
        await SeedProjectAsync(operatorScope);
        var foreignEnvironment = await SeedProjectAndEnvironmentAsync(otherProjectScope, "site-a2", "dir-a2.contoso.local");
        var (username, password) = await SeedUserAsync(operatorScope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, foreignEnvironment, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountCommandsAsync(operatorScope));
        var events = await AuditAsync(operatorScope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "not-found-or-not-authorized");
    }

    [Fact]
    public async Task RequestForEnvironmentInAnotherTenantReturns404()
    {
        var operatorScope = SqlServerFixture.NewScope();
        var otherTenantScope = SqlServerFixture.NewScope();
        await SeedProjectAsync(operatorScope);
        var foreignEnvironment = await SeedProjectAndEnvironmentAsync(otherTenantScope, "site-b1", "dir-b1.contoso.local");
        var (username, password) = await SeedUserAsync(operatorScope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, foreignEnvironment, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // externamente idêntico ao caso cross-project
        Assert.Equal(0, await CountCommandsAsync(operatorScope));
    }

    // ---- §30 idempotência HTTP (mesmo key ⇒ replay, 1 Job).

    [Fact]
    public async Task SameIdempotencyKeyReplaysSameJob()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-idem", "dir-idem.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var first = await PostRequestDiscoveryAsync(client, environmentId, idempotencyKey);
        using var second = await PostRequestDiscoveryAsync(client, environmentId, idempotencyKey);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(
            first.Headers.Location!.OriginalString,
            second.Headers.Location!.OriginalString); // mesmo jobId no destino

        Assert.Equal(1, await CountCommandsAsync(scope)); // exatamente 1 Job / 1 comando
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && e.Succeeded && e.Reason == "accepted");
        Assert.Contains(events, e => e.Username == username && e.Succeeded && e.Reason == "idempotent-replay");
    }

    // ---- §31 conflito de idempotência (mesma key, ambiente diferente) ⇒ 409, 1 Job.

    [Fact]
    public async Task SameKeyDifferentEnvironmentReturns409Conflict()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentA = await SeedProjectAndEnvironmentAsync(scope, "site-x", "dir-x.contoso.local");
        var environmentB = await SeedEnvironmentAsync(scope, "site-y", "dir-y.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var first = await PostRequestDiscoveryAsync(client, environmentA, idempotencyKey);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        using var second = await PostRequestDiscoveryAsync(client, environmentB, idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode); // 409

        Assert.Equal(1, await CountCommandsAsync(scope)); // nenhum Job novo
        var events = await AuditAsync(scope);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "idempotency-conflict");
    }

    // ---- §15 validação de input.

    [Fact]
    public async Task EmptyEnvironmentIdReturns400AndCreatesNoJob()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, Guid.Empty, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountCommandsAsync(scope));
    }

    // ---- §29 autoridade server-side: campos maliciosos do formulário são IGNORADOS.

    [Fact]
    public async Task MaliciousExtraFormFieldsAreIgnoredContextIsResolvedServerSide()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-authority", "dir-authority.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var maliciousCorrelation = Guid.NewGuid();

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        var token = await GetAntiforgeryTokenAsync(client, "/EnterpriseVault/Index");
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("EnvironmentId", environmentId.ToString("D")),
            new KeyValuePair<string, string>("IdempotencyKey", Guid.NewGuid().ToString("D")),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            // Campos maliciosos: NÃO devem ser vinculados/persistidos.
            new KeyValuePair<string, string>("TenantId", Guid.NewGuid().ToString("D")),
            new KeyValuePair<string, string>("ProjectId", Guid.NewGuid().ToString("D")),
            new KeyValuePair<string, string>("SiteName", "evil-site"),
            new KeyValuePair<string, string>("DirectoryServer", "evil-directory"),
            new KeyValuePair<string, string>("ConfigurationVersion", "999"),
            new KeyValuePair<string, string>("ConfigurationHash", new string('e', 64)),
            new KeyValuePair<string, string>("DiscoveryPolicyVersion", "999"),
            new KeyValuePair<string, string>("RequestedBy", "hacker@evil.example"),
            new KeyValuePair<string, string>("CorrelationId", maliciousCorrelation.ToString("D")),
            new KeyValuePair<string, string>("Role", "Administrator"),
            new KeyValuePair<string, string>("JobId", Guid.NewGuid().ToString("D")),
        ]);
        using var response = await client.PostAsync(
            new Uri("/EnterpriseVault/Index?handler=RequestDiscovery", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var command = await ReadCommandAsync(scope);
        var project = await ReadProjectConfigAsync(scope);
        Assert.Equal(scope.Tenant.Value, command.TenantId);            // do principal, não do formulário
        Assert.Equal(scope.Project.Value, command.ProjectId);         // do principal, não do formulário
        Assert.Equal(environmentId, command.EnvironmentId);
        Assert.Equal("site-authority", command.SiteName);             // do SQL, não "evil-site"
        Assert.Equal("dir-authority.contoso.local", command.DirectoryServer); // do SQL, não "evil-directory"
        Assert.Equal(username, command.RequestedBy);                  // User.Identity.Name, não "hacker"
        Assert.Equal(project.ConfigurationVersion, command.ExpectedConfigurationVersion); // versão do projeto no SQL
        Assert.Equal(project.ConfigurationHash, command.ExpectedConfigurationHash);       // hash do projeto no SQL
        Assert.Equal(EvDiscoveryPolicy.Default.PolicyVersion, command.DiscoveryPolicyVersion); // política do servidor
        Assert.NotEqual(maliciousCorrelation, command.CorrelationId); // correlação criada no servidor
        Assert.NotEqual(999, command.ExpectedConfigurationVersion);
    }

    // ---- §32 falha de auditoria após enqueue: 500 fail-closed; Job persiste; retry idempotente.

    [Fact]
    public async Task AuditFailureAfterEnqueueReturns500AndRetryIsIdempotent()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = await SeedProjectAndEnvironmentAsync(scope, "site-audit", "dir-audit.contoso.local");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);
        var idempotencyKey = Guid.NewGuid();

        // Primeira tentativa: auditoria FALHA deliberadamente ⇒ 500, mas o Job durável já foi enfileirado.
        using (var failingFactory = CreateFactory(
            discoveryEnabled: true,
            configureServices: services =>
            {
                services.RemoveAll<IPortalOperationalAudit>();
                services.AddScoped<IPortalOperationalAudit>(_ => new FailingOperationalAudit());
            }))
        using (var client = failingFactory.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var response = await PostRequestDiscoveryAsync(client, environmentId, idempotencyKey);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); // 500 fail-closed
        }

        Assert.Equal(1, await CountCommandsAsync(scope));   // o Job durável permanece
        var jobId = await SingleCommandJobIdAsync(scope);

        // Retry com a MESMA chave e auditoria normal: devolve o MESMO Job (idempotência), sem criar outro.
        using (var okFactory = CreateFactory(discoveryEnabled: true))
        using (var client = okFactory.CreateClient(NoRedirect()))
        {
            await LoginAsync(client, username, password);
            using var retry = await PostRequestDiscoveryAsync(client, environmentId, idempotencyKey);
            Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
            Assert.Contains(jobId.ToString("D"), retry.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1, await CountCommandsAsync(scope));   // ainda exatamente 1 Job
    }

    // ============================ infra de teste ============================

    private WebApplicationFactory<Program> CreateFactory(
        bool discoveryEnabled, Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", _fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", _fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", _fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
            builder.UseSetting("EnterpriseVaultDiscovery:Enabled", discoveryEnabled ? "true" : "false");
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
            new PortalUserRegistration(username, "Stage4 " + role, scope.Tenant, scope.Project, Hasher.Hash(password), [role]),
            CancellationToken.None);
        return (username, password);
    }

    private async Task<Guid> SeedProjectAndEnvironmentAsync(TenantScope scope, string site, string dir)
    {
        await SeedProjectAsync(scope);
        return await SeedEnvironmentAsync(scope, site, dir);
    }

    private async Task SeedProjectAsync(TenantScope scope)
    {
        var project = Slice2Support.NewProject(scope);
        await Slice2Support.ProjectStore(_fixture).AddAsync(project, CorrelationId.New(), CancellationToken.None);
    }

    private async Task<Guid> SeedEnvironmentAsync(TenantScope scope, string site, string dir)
    {
        var environmentId = Guid.NewGuid();
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
            VALUES (@env, @tenant, @project, @site, @dir);
            """, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = site });
        command.Parameters.Add(new SqlParameter("@dir", SqlDbType.NVarChar, 260) { Value = dir });
        await command.ExecuteNonQueryAsync();
        return environmentId;
    }

    private async Task<int> CountCommandsAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.ev_discovery_commands WHERE project_id = @project;", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<Guid> SingleCommandJobIdAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT job_id FROM dbo.ev_discovery_commands WHERE project_id = @project;", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<CommandRow> ReadCommandAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            SELECT tenant_id, project_id, environment_id, site_name, directory_server, requested_by,
                   expected_project_configuration_version, expected_configuration_hash,
                   discovery_policy_version, correlation_id
            FROM dbo.ev_discovery_commands WHERE project_id = @project;
            """, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new CommandRow(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetInt32(6), reader.GetString(7).Trim(), reader.GetInt32(8), reader.GetGuid(9));
    }

    private async Task<ProjectConfigRow> ReadProjectConfigAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT configuration_version, configuration_hash FROM dbo.migration_projects WHERE project_id = @project;",
            tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProjectConfigRow(reader.GetInt32(0), reader.GetString(1).Trim());
    }

    private Task<IReadOnlyList<PortalOperationalAuditEvent>> AuditAsync(TenantScope scope) =>
        new SqlPortalOperationalAudit(_fixture.Factory).RecentAsync(scope, 100, CancellationToken.None);

    private static async Task<HttpResponseMessage> PostRequestDiscoveryAsync(HttpClient client, Guid environmentId, Guid idempotencyKey)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/EnterpriseVault/Index");
        var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("EnvironmentId", environmentId.ToString("D")),
            new KeyValuePair<string, string>("IdempotencyKey", idempotencyKey.ToString("D")),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]);
        return await client.PostAsync(new Uri("/EnterpriseVault/Index?handler=RequestDiscovery", UriKind.Relative), content);
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
        // Login redireciona (302) para o painel quando bem-sucedido (cliente sem auto-redirect).
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

    private sealed record CommandRow(
        Guid TenantId, Guid ProjectId, Guid EnvironmentId, string SiteName, string DirectoryServer,
        string RequestedBy, int ExpectedConfigurationVersion, string ExpectedConfigurationHash,
        int DiscoveryPolicyVersion, Guid CorrelationId);

    private sealed record ProjectConfigRow(int ConfigurationVersion, string ConfigurationHash);

    private sealed class FailingOperationalAudit : IPortalOperationalAudit
    {
        public Task RecordAsync(TenantScope scope, PortalOperationalAuditEvent auditEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha deliberada de auditoria operacional (teste).");

        public Task<IReadOnlyList<PortalOperationalAuditEvent>> RecentAsync(TenantScope scope, int max, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha deliberada de auditoria operacional (teste).");
    }
}
