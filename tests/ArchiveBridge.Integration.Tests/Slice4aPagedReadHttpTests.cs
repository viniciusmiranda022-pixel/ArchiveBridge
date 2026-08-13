using System.Data;
using System.Net;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Passo 5 — histórico de evidências e auditoria paginados em nível HTTP real (WebApplicationFactory + SQL).
/// Prova 200 com filtros/paginação, 400 fail-closed para cursor inválido, ausência de metadados cross-scope no
/// HTML, preservação de filtros no link "Próxima", o RBAC inalterado da auditoria e que NENHUMA navegação de
/// leitura gera evento de auditoria (não há recursão). O download verificado permanece inalterado.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aPagedReadHttpTests : IDisposable
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private static readonly DateTimeOffset Base = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly SqlServerFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;

    public Slice4aPagedReadHttpTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
        });
    }

    [Fact]
    public async Task EvidenceHistoryRendersFiltersPaginationAndPreservesDownloadLink()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var envA = Guid.NewGuid();
        var envB = Guid.NewGuid();
        await SeedEnvironmentAsync(scope, envA, "prod-alpha");
        await SeedEnvironmentAsync(scope, envB, "prod-beta");
        await SeedRunAsync(scope, envA, 1, "logical/a/v1");
        await SeedRunAsync(scope, envB, 1, "logical/b/v1");
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, password);

        // Página 1 com tamanho 1 ⇒ 1 item + link "Próxima".
        using var page1 = await client.GetAsync(new Uri("/Evidence/Index?size=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, page1.StatusCode);
        var html1 = await page1.Content.ReadAsStringAsync();
        Assert.Contains("/Evidence/Download", html1, StringComparison.Ordinal); // link de download preservado
        var nextHref = ExtractNextHref(html1);
        Assert.NotNull(nextHref);
        Assert.Contains("cursor=", nextHref!, StringComparison.Ordinal);

        // Seguir o cursor entrega a próxima fatia (200), sem repetir o item anterior.
        using var page2 = await client.GetAsync(new Uri(WebUtility.HtmlDecode(nextHref!), UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);

        // Filtro por prefixo de site funciona.
        using var filtered = await client.GetAsync(new Uri("/Evidence/Index?site=prod-alpha", UriKind.Relative));
        var htmlFiltered = await filtered.Content.ReadAsStringAsync();
        Assert.Contains("prod-alpha", htmlFiltered, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-beta", htmlFiltered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceHistoryRejectsInvalidCursorWith400()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, username, password);

        using var response = await client.GetAsync(new Uri("/Evidence/Index?cursor=not-a-valid-cursor", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EvidenceHistoryDoesNotLeakOtherProjectMetadata()
    {
        var scope1 = SqlServerFixture.NewScope();
        var scope2 = new TenantScope(scope1.Tenant, new ProjectId(Guid.NewGuid()));
        await SeedProjectAsync(scope1);
        await SeedProjectAsync(scope2);
        var foreignEnv = Guid.NewGuid();
        await SeedEnvironmentAsync(scope2, foreignEnv, "secret-foreign-site");
        await SeedRunAsync(scope2, foreignEnv, 1, "logical/foreign/v1");
        var (username, password) = await SeedUserAsync(scope1, PortalRoles.Viewer);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, password);

        using var response = await client.GetAsync(new Uri("/Evidence/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-foreign-site", html, StringComparison.Ordinal); // nada cross-project no HTML
    }

    [Fact]
    public async Task AuditIsAllowedForAuditorAndAdministratorAndDeniedForViewer()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);

        var (auditor, auditorPw) = await SeedUserAsync(scope, PortalRoles.Auditor);
        using (var client = _factory.CreateClient())
        {
            await LoginAsync(client, auditor, auditorPw);
            using var response = await client.GetAsync(new Uri("/Audit/Index", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Trilha de auditoria", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var (admin, adminPw) = await SeedUserAsync(scope, PortalRoles.Administrator);
        using (var client = _factory.CreateClient())
        {
            await LoginAsync(client, admin, adminPw);
            using var response = await client.GetAsync(new Uri("/Audit/Index", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var (viewer, viewerPw) = await SeedUserAsync(scope, PortalRoles.Viewer);
        using (var client = _factory.CreateClient())
        {
            await LoginAsync(client, viewer, viewerPw);
            using var response = await client.GetAsync(new Uri("/Audit/Index", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // segue o redirect para /Account/Denied
            Assert.Contains("Acesso negado", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AuditRejectsInvalidCursorWith400()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (auditor, auditorPw) = await SeedUserAsync(scope, PortalRoles.Auditor);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, auditor, auditorPw);

        using var response = await client.GetAsync(new Uri("/Audit/Index?opCursor=@@invalid@@", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ViewingAndPaginatingAuditCreatesNoOperationalAuditEvent()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (auditor, auditorPw) = await SeedUserAsync(scope, PortalRoles.Auditor);

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var before = (await audit.RecentAsync(scope, 500, CancellationToken.None)).Count;

        using var client = _factory.CreateClient();
        await LoginAsync(client, auditor, auditorPw);
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(new Uri("/Audit/Index?opSize=25", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var after = (await audit.RecentAsync(scope, 500, CancellationToken.None)).Count;
        Assert.Equal(before, after); // visualizar/paginar auditoria NÃO gera evento (sem recursão)
    }

    [Fact]
    public async Task EvidenceHistoryRejectsInvertedDateRangeWith400()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, username, password);

        // From > To ⇒ 400 fail-closed (nenhuma query executa).
        using var response = await client.GetAsync(new Uri(
            "/Evidence/Index?from=2027-01-02T00:00:00Z&to=2027-01-01T00:00:00Z", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuditRejectsInvalidAuthCursorWith400()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (auditor, auditorPw) = await SeedUserAsync(scope, PortalRoles.Auditor);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, auditor, auditorPw);

        // authCursor inválido ⇒ 400 (prova equivalente à do opCursor, para a lista de autenticação).
        using var response = await client.GetAsync(new Uri("/Audit/Index?authCursor=@@invalid@@", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuditRejectsAuthCursorWithDivergentFiltersWith400()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (auditor, auditorPw) = await SeedUserAsync(scope, PortalRoles.Auditor);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, auditor, auditorPw);

        // Cursor bem-formado, gerado com o fingerprint dos filtros VAZIOS; reusado com um filtro divergente
        // (authReason) ⇒ o fingerprint recomputado não casa ⇒ 400.
        var cursor = KeysetCursor.Encode(
            FilterFingerprint.Compute(null, null, null, null, null), new AuditSeekPosition(Base, 5));
        using var response = await client.GetAsync(new Uri(
            $"/Audit/Index?authReason=login-failed&authCursor={cursor}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuditOperationalAndSignInPaginateIndependently()
    {
        var scope = SqlServerFixture.NewScope();
        await SeedProjectAsync(scope);
        var (auditor, auditorPw) = await SeedUserAsync(scope, PortalRoles.Auditor);
        var userId = await FindUserIdAsync(auditor);
        await SeedOperationalEventsAsync(scope, userId, 60);
        await SeedSignInEventsAsync(scope.Tenant.Value, 60);

        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        var before = (await audit.RecentAsync(scope, 500, CancellationToken.None)).Count;

        using var client = _factory.CreateClient();
        await LoginAsync(client, auditor, auditorPw);

        // Página inicial: ambas as listas têm "Próxima"; a auth carrega um filtro (authSucceeded=true).
        var html1 = await GetStringAsync(client, "/Audit/Index?opSize=25&authSize=25&authSucceeded=true");
        var (opNext1, authNext1) = ExtractAuditNextLinks(html1);
        Assert.NotNull(opNext1);
        Assert.NotNull(authNext1);
        // "Próxima" da operacional: muda opCursor, preserva o filtro de auth e NÃO toca no cursor de auth.
        Assert.Contains("opCursor=", opNext1!, StringComparison.Ordinal);
        Assert.Contains("authSucceeded=true", opNext1!, StringComparison.Ordinal);
        Assert.DoesNotContain("authCursor=", opNext1!, StringComparison.Ordinal);
        // "Próxima" da auth: muda authCursor, preserva o filtro; op ainda na página 1 (sem opCursor).
        Assert.Contains("authCursor=", authNext1!, StringComparison.Ordinal);
        Assert.Contains("authSucceeded=true", authNext1!, StringComparison.Ordinal);
        Assert.DoesNotContain("opCursor=", authNext1!, StringComparison.Ordinal);

        // Avança a OPERACIONAL para a página 2.
        var html2 = await GetStringAsync(client, WebUtility.HtmlDecode(opNext1!));
        var (opNext2, authNext2) = ExtractAuditNextLinks(html2);
        Assert.NotNull(opNext2);
        Assert.NotNull(authNext2);
        Assert.NotEqual(QueryValue(opNext1!, "opCursor"), QueryValue(opNext2!, "opCursor")); // op avançou
        // A "Próxima" da auth nesta página preserva o estado da operacional (op page-2) + o filtro de auth.
        Assert.Contains("authCursor=", authNext2!, StringComparison.Ordinal);
        Assert.Contains("opCursor=", authNext2!, StringComparison.Ordinal);
        Assert.Contains("authSucceeded=true", authNext2!, StringComparison.Ordinal);

        // Agora avança a AUTH, preservando a operacional na página 2.
        var html3 = await GetStringAsync(client, WebUtility.HtmlDecode(authNext2!));
        var (opNext3, _) = ExtractAuditNextLinks(html3);
        Assert.NotNull(opNext3);
        // A operacional permaneceu na página 2 (mesmo opCursor de destino que em html2): estado preservado.
        Assert.Equal(QueryValue(opNext2!, "opCursor"), QueryValue(opNext3!, "opCursor"));

        // Nenhuma dessas navegações de leitura gerou evento operacional (sem recursão de auditoria).
        var after = (await audit.RecentAsync(scope, 500, CancellationToken.None)).Count;
        Assert.Equal(before, after);
    }

    private static string? ExtractNextHref(string html)
    {
        // O link "Próxima" resolve para /Evidence (Index é a página padrão) e é o único com "cursor=".
        var match = Regex.Match(html, "href=\"(/Evidence[^\"]*cursor=[^\"]*)\"", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    // Extrai os DOIS links "Próxima" da página de auditoria — um por seção (operacional e autenticação),
    // separadas pelo cabeçalho "<h2>Autenticação</h2>". Cada link resolve para /Audit (Index é a página padrão).
    private static (string? OpNext, string? AuthNext) ExtractAuditNextLinks(string html)
    {
        var split = html.IndexOf("<h2>Autenticação</h2>", StringComparison.Ordinal);
        var opSection = split >= 0 ? html[..split] : html;
        var authSection = split >= 0 ? html[split..] : string.Empty;
        return (ExtractAuditHref(opSection), ExtractAuditHref(authSection));
    }

    private static string? ExtractAuditHref(string section)
    {
        var match = Regex.Match(section, "href=\"(/Audit\\?[^\"]*)\"[^>]*>Próxima", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string QueryValue(string href, string key)
    {
        var match = Regex.Match(href, Regex.Escape(key) + "=([^&\"]*)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string relativeUri)
    {
        using var response = await client.GetAsync(new Uri(relativeUri, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<Guid> FindUserIdAsync(string username)
    {
        var record = await new SqlPortalUserStore(_fixture.ConnectionString)
            .FindByUsernameAsync(username, CancellationToken.None);
        return record!.UserId;
    }

    private async Task SeedOperationalEventsAsync(TenantScope scope, Guid userId, int count)
    {
        var audit = new SqlPortalOperationalAudit(_fixture.Factory);
        for (var i = 0; i < count; i++)
        {
            await audit.RecordAsync(scope, new PortalOperationalAuditEvent(
                userId, "opuser", "op.act", "res", $"res{i:D4}", true, "reason", Guid.NewGuid(), Base.AddSeconds(i)),
                CancellationToken.None);
        }
    }

    private async Task SeedSignInEventsAsync(Guid tenantId, int count)
    {
        var audit = new SqlPortalSignInAudit(_fixture.ConnectionString);
        for (var i = 0; i < count; i++)
        {
            await audit.RecordAsync(new PortalSignInEvent(
                tenantId, null, null, "signin-user", true, $"r{i:D4}", "127.0.0.1", Guid.NewGuid(), Base.AddSeconds(i)),
                CancellationToken.None);
        }
    }

    private async Task<(string Username, string Password)> SeedUserAsync(TenantScope scope, string role)
    {
        var username = "u_" + Guid.NewGuid().ToString("N");
        const string password = "Str0ng!pw";
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Passo5 " + role, scope.Tenant, scope.Project, Hasher.Hash(password), [role]),
            CancellationToken.None);
        return (username, password);
    }

    private async Task SeedProjectAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "INSERT INTO dbo.projects (project_id, tenant_id) VALUES (@project, @tenant);", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedEnvironmentAsync(TenantScope scope, Guid environmentId, string site)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
            VALUES (@env, @tenant, @project, @site, 'directory-test');
            """, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = site });
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedRunAsync(TenantScope scope, Guid environmentId, int version, string logicalPath)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.ev_discovery_runs
                (environment_id, discovery_version, tenant_id, project_id, run_id, status, result_status, result_code,
                 observed_version, discovery_schema_version, configuration_hash, evidence_hash, content_sha256,
                 evidence_path, evidence_size_bytes, correlation_id, started_at_utc, completed_at_utc)
            VALUES
                (@env, @version, @tenant, @project, @run, 2, 2, 'EV_READY',
                 '15.1', 1, @configHash, @semanticHash, @contentHash, @path, 1024, @correlation,
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            """, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@env", SqlDbType.UniqueIdentifier) { Value = environmentId });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = new string('a', 64) });
        command.Parameters.Add(new SqlParameter("@semanticHash", SqlDbType.Char, 64) { Value = new string('b', 64) });
        command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = new string('c', 64) });
        command.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 400) { Value = logicalPath });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        await command.ExecuteNonQueryAsync();
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
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect);
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

    public void Dispose() => _factory.Dispose();
}
