using System.Data;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A — Portal em nível HTTP, ligado ao SQL Server real e ao filesystem real de evidências. Prova
/// autenticação/RBAC e, para evidência, bytes exatos, validação de bundle/hash/tamanho, auditoria e anti-IDOR.
/// Nenhuma capacidade do Slice 4B é exercida.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aPortalHttpTests : IDisposable
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private readonly SqlServerFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;

    public Slice4aPortalHttpTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Development: sem redirect HTTPS/HSTS de produção, para exercitar o pipeline sobre HTTP de teste.
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty); // sem bootstrap automático
        });
    }

    [Fact]
    public async Task HealthLiveIsAnonymousAndOk()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReadyChecksDatabaseAndIsOk()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ready", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedPageRedirectsToLoginWhenAnonymous()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginPageRendersAnonymously()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Portal Operacional", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditorCanSignInAndReadButIsDeniedAdministration()
    {
        var scope = SqlServerFixture.NewScope();
        var username = "auditor_" + Guid.NewGuid().ToString("N");
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Auditor", scope.Tenant, scope.Project, Hasher.Hash("Aud1tor!pw"),
                [PortalRoles.Auditor]),
            CancellationToken.None);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "Aud1tor!pw");

        using var jobs = await client.GetAsync(new Uri("/Jobs/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.Contains("Jobs duráveis", await jobs.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var admin = await client.GetAsync(new Uri("/Admin/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("Acesso negado", await admin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministratorCanReachAdministration()
    {
        var scope = SqlServerFixture.NewScope();
        var username = "admin_" + Guid.NewGuid().ToString("N");
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Admin", scope.Tenant, scope.Project, Hasher.Hash("Adm1n!pw"),
                [PortalRoles.Administrator]),
            CancellationToken.None);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "Adm1n!pw");

        using var admin = await client.GetAsync(new Uri("/Admin/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains("Administração do portal", await admin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewerIsDeniedAuditTrail()
    {
        var scope = SqlServerFixture.NewScope();
        var username = "viewer_" + Guid.NewGuid().ToString("N");
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Viewer", scope.Tenant, scope.Project, Hasher.Hash("V1ewer!pw"),
                [PortalRoles.Viewer]),
            CancellationToken.None);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "V1ewer!pw");

        using var audit = await client.GetAsync(new Uri("/Audit/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Contains("Acesso negado", await audit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditorCanReadAuditTrail()
    {
        var scope = SqlServerFixture.NewScope();
        var username = "auditread_" + Guid.NewGuid().ToString("N");
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Auditor", scope.Tenant, scope.Project, Hasher.Hash("Aud1t!pw"),
                [PortalRoles.Auditor]),
            CancellationToken.None);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "Aud1t!pw");

        using var audit = await client.GetAsync(new Uri("/Audit/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Contains("Trilha de auditoria", await audit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiedEvidenceDownloadReturnsExactBytesAndAuditsSuccess()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = Guid.NewGuid();
        const int version = 1;
        var username = "evidence_" + Guid.NewGuid().ToString("N");
        var evidenceBytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"proof\":\"bytes-must-not-change\"}\n");

        await SeedBaseProjectAsync(scope);
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Evidence Viewer", scope.Tenant, scope.Project, Hasher.Hash("Ev1dence!pw"),
                [PortalRoles.Viewer]),
            CancellationToken.None);

        var reference = await PublishEvidenceAsync(scope, environmentId, version, evidenceBytes);
        await SeedEvidenceRunAsync(scope, environmentId, version, reference);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "Ev1dence!pw");
        using var response = await client.GetAsync(new Uri(
            $"/Evidence/Download?environmentId={environmentId:D}&version={version}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var downloaded = await response.Content.ReadAsByteArrayAsync();
        Assert.True(evidenceBytes.SequenceEqual(downloaded), "evidence.json deve ser entregue byte-a-byte, sem reserialização.");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("evidence.json", response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty, StringComparison.Ordinal);

        var events = await new SqlPortalOperationalAudit(_fixture.Factory).RecentAsync(scope, 100, CancellationToken.None);
        Assert.Contains(events, e => e.Username == username && e.ActionCode == "evidence.download" && e.Succeeded && e.Reason == "verified");
    }

    [Fact]
    public async Task TamperedEvidenceBundleIsRejectedFailClosedAndAudited()
    {
        var scope = SqlServerFixture.NewScope();
        var environmentId = Guid.NewGuid();
        const int version = 1;
        var username = "tamper_" + Guid.NewGuid().ToString("N");
        var evidenceBytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"proof\":\"original\"}\n");

        await SeedBaseProjectAsync(scope);
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Tamper Viewer", scope.Tenant, scope.Project, Hasher.Hash("Tamp3r!pw"),
                [PortalRoles.Viewer]),
            CancellationToken.None);

        var reference = await PublishEvidenceAsync(scope, environmentId, version, evidenceBytes);
        await SeedEvidenceRunAsync(scope, environmentId, version, reference);

        var physical = Path.Combine(
            _fixture.ArtifactRoot,
            reference.LogicalPath.Replace('/', Path.DirectorySeparatorChar),
            "evidence.json");
        await File.WriteAllBytesAsync(physical, Encoding.UTF8.GetBytes("{\"tampered\":true}\n"));

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "Tamp3r!pw");
        using var response = await client.GetAsync(new Uri(
            $"/Evidence/Download?environmentId={environmentId:D}&version={version}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var events = await new SqlPortalOperationalAudit(_fixture.Factory).RecentAsync(scope, 100, CancellationToken.None);
        Assert.Contains(events, e => e.Username == username && e.ActionCode == "evidence.download" && !e.Succeeded);
    }

    [Fact]
    public async Task EvidenceDownloadIsAntiIdorAcrossProjectsInSameTenant()
    {
        var scope1 = SqlServerFixture.NewScope();
        var scope2 = new TenantScope(scope1.Tenant, new ProjectId(Guid.NewGuid()));
        var environmentId = Guid.NewGuid();
        const int version = 1;
        var username = "idor_" + Guid.NewGuid().ToString("N");
        var evidenceBytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}\n");

        await SeedBaseProjectAsync(scope1);
        await SeedBaseProjectAsync(scope2);
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Project 1 Viewer", scope1.Tenant, scope1.Project, Hasher.Hash("Id0r!pw"),
                [PortalRoles.Viewer]),
            CancellationToken.None);

        var reference = await PublishEvidenceAsync(scope2, environmentId, version, evidenceBytes);
        await SeedEvidenceRunAsync(scope2, environmentId, version, reference);

        using var client = _factory.CreateClient();
        await LoginAsync(client, username, "Id0r!pw");
        using var response = await client.GetAsync(new Uri(
            $"/Evidence/Download?environmentId={environmentId:D}&version={version}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // não revela existência em outro projeto
        var events = await new SqlPortalOperationalAudit(_fixture.Factory).RecentAsync(scope1, 100, CancellationToken.None);
        Assert.Contains(events, e => e.Username == username && !e.Succeeded && e.Reason == "not-found-or-not-authorized");
    }

    [Fact]
    public async Task WrongPasswordDoesNotAuthenticate()
    {
        var scope = SqlServerFixture.NewScope();
        var username = "wrong_" + Guid.NewGuid().ToString("N");
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "User", scope.Tenant, scope.Project, Hasher.Hash("right-pw"),
                [PortalRoles.Operator]),
            CancellationToken.None);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        using var content = FormContent(username, "wrong-pw", token);
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);

        using var protectedResponse = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Found, protectedResponse.StatusCode);
        Assert.Contains("/Account/Login", protectedResponse.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    private async Task<EvDiscoveryEvidenceReference> PublishEvidenceAsync(
        TenantScope scope,
        Guid environmentId,
        int version,
        byte[] evidenceBytes)
    {
        var store = new FileSystemEvDiscoveryEvidenceStore(_fixture.ArtifactRoot);
        var hash = DeterministicHash.ComputeBytes(evidenceBytes);
        var staged = await store.StageAsync(evidenceBytes, hash, CancellationToken.None);
        return await store.PublishAsync(
            staged,
            new EvDiscoveryEvidenceDescriptor(scope, new EvEnvironmentId(environmentId), version),
            CancellationToken.None);
    }

    private async Task SeedBaseProjectAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "INSERT INTO dbo.projects (project_id, tenant_id) VALUES (@project, @tenant);", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedEvidenceRunAsync(
        TenantScope scope,
        Guid environmentId,
        int version,
        EvDiscoveryEvidenceReference reference)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);

        await using (var environment = new SqlCommand(
            """
            INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
            VALUES (@environment, @tenant, @project, 'Portal-Test', 'directory-test');
            """, tenant.Connection))
        {
            environment.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId });
            environment.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
            environment.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await environment.ExecuteNonQueryAsync();
        }

        await using var run = new SqlCommand(
            """
            INSERT INTO dbo.ev_discovery_runs
                (environment_id, discovery_version, tenant_id, project_id, run_id, status, result_status, result_code,
                 observed_version, discovery_schema_version, configuration_hash, evidence_hash, content_sha256,
                 evidence_path, evidence_size_bytes, correlation_id, started_at_utc, completed_at_utc)
            VALUES
                (@environment, @version, @tenant, @project, @run, 2, 2, 'EV_READY',
                 '15.1', 1, @configHash, @semanticHash, @contentHash,
                 @path, @size, @correlation, SYSUTCDATETIME(), SYSUTCDATETIME());
            """, tenant.Connection);
        run.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId });
        run.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        run.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        run.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        run.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        run.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = new string('a', 64) });
        run.Parameters.Add(new SqlParameter("@semanticHash", SqlDbType.Char, 64) { Value = new string('b', 64) });
        run.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = reference.ContentSha256.Value });
        run.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 1000) { Value = reference.LogicalPath });
        run.Parameters.Add(new SqlParameter("@size", SqlDbType.BigInt) { Value = reference.SizeBytes });
        run.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        await run.ExecuteNonQueryAsync();
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        using var content = FormContent(username, password, token);
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Marcador pós-login: o painel enterprise usa "Visão Geral" como título (antes "Painel operacional").
        Assert.Contains("Visão Geral", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Token antiforgery não encontrado no formulário de login.");
        return match.Groups[1].Value;
    }

    private static FormUrlEncodedContent FormContent(string username, string password, string token) =>
        new(
        [
            new KeyValuePair<string, string>("Username", username),
            new KeyValuePair<string, string>("Password", password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]);

    public void Dispose() => _factory.Dispose();
}
