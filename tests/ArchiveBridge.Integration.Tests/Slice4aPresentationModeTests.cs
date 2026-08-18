using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.ControlPlane.Composition;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting.Internal;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Frente UX/UI — Modo de Demonstração (Presentation Mode). Prova as garantias inegociáveis: default
/// desabilitado; permitido apenas em Development/Staging; fail-closed no startup em Produção; ZERO escritas
/// de negócio quando ativo; banner + dados sintéticos; e que a navegação e o RBAC continuam intactos (nenhuma
/// proteção existente foi enfraquecida).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aPresentationModeTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private readonly SqlServerFixture _fixture = fixture;

    // ============================ fail-closed por ambiente (unidade) ============================

    [Fact]
    public void PresentationModeDefaultsToDisabled()
    {
        Assert.False(new PresentationModeOptions().Enabled);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void EnabledIsAllowedInDevelopmentAndStaging(string environment)
    {
        var options = new PresentationModeOptions { Enabled = true };
        // Não lança.
        options.EnsureAllowedOrThrow(new HostingEnvironment { EnvironmentName = environment });
    }

    [Fact]
    public void EnabledInProductionThrowsFailClosed()
    {
        var options = new PresentationModeOptions { Enabled = true };
        var exception = Assert.Throws<InvalidOperationException>(
            () => options.EnsureAllowedOrThrow(new HostingEnvironment { EnvironmentName = "Production" }));
        Assert.Contains("PresentationMode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledNeverThrowsEvenInProduction()
    {
        var options = new PresentationModeOptions { Enabled = false };
        // Default fail-closed: desabilitado nunca bloqueia startup, em qualquer ambiente.
        options.EnsureAllowedOrThrow(new HostingEnvironment { EnvironmentName = "Production" });
    }

    // ============================ fail-closed no startup (web) ============================

    [Fact]
    public void PresentationModeEnabledInProductionAbortsStartup()
    {
        using var factory = CreateFactory("Production", presentation: true, discovery: false);
        var exception = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(exception);
        Assert.Contains("PresentationMode", Flatten(exception!), StringComparison.Ordinal);
    }

    // ============================ ZERO escritas de negócio em demo ============================

    [Fact]
    public async Task PresentationModeRefusesDiscoveryRequestAndWritesNothing()
    {
        var scope = SqlServerFixture.NewScope();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        // Gate de descoberta HABILITADO de propósito: mesmo assim, o modo demonstração recusa ANTES de
        // qualquer store — provando que a recusa não depende do gate, e sim do modo.
        using var factory = CreateFactory("Development", presentation: true, discovery: true);
        using var client = factory.CreateClient(NoRedirect());
        await LoginAsync(client, username, password);

        using var response = await PostRequestDiscoveryAsync(client, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountCommandsAsync(scope)); // nenhum Job/comando criado
    }

    [Fact]
    public async Task PresentationModeShowsDemoBannerAndSyntheticData()
    {
        var scope = SqlServerFixture.NewScope();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory("Development", presentation: true, discovery: false);
        using var client = factory.CreateClient();
        await LoginAsync(client, username, password);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Modo demonstração", html, StringComparison.Ordinal);
        Assert.Contains("Contoso Demo", html, StringComparison.Ordinal); // dataset sintético
    }

    // ============================ navegação e RBAC intactos ============================

    [Fact]
    public async Task MainNavigationLinksRender()
    {
        var scope = SqlServerFixture.NewScope();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        using var factory = CreateFactory("Development", presentation: false, discovery: false);
        using var client = factory.CreateClient();
        await LoginAsync(client, username, password);

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        foreach (var label in new[] { "Projetos", "Ondas de Migração", "Enterprise Vault", "Mapping", "Evidências", "Auditoria", "Jobs" })
        {
            Assert.Contains(label, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DemoModeStillRedirectsAnonymousToLogin()
    {
        using var factory = CreateFactory("Development", presentation: true, discovery: false);
        using var client = factory.CreateClient(NoRedirect());
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DemoModeStillEnforcesAuditRbacForViewer()
    {
        var scope = SqlServerFixture.NewScope();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Viewer);

        using var factory = CreateFactory("Development", presentation: true, discovery: false);
        using var client = factory.CreateClient();
        await LoginAsync(client, username, password);

        using var response = await client.GetAsync(new Uri("/Audit/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Acesso negado", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ============================ infra de teste ============================

    private WebApplicationFactory<Program> CreateFactory(string environment, bool presentation, bool discovery) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", environment);
            builder.UseSetting("ConnectionStrings:Application", _fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", _fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", _fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
            builder.UseSetting("EnterpriseVaultDiscovery:Enabled", discovery ? "true" : "false");
            builder.UseSetting("PresentationMode:Enabled", presentation ? "true" : "false");
        });

    private static WebApplicationFactoryClientOptions NoRedirect() => new() { AllowAutoRedirect = false };

    private async Task<(string Username, string Password)> SeedUserAsync(TenantScope scope, string role)
    {
        var username = "demo_" + Guid.NewGuid().ToString("N");
        const string password = "Dem0!pw";
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Demo " + role, scope.Tenant, scope.Project, Hasher.Hash(password), [role]),
            CancellationToken.None);
        return (username, password);
    }

    private async Task<int> CountCommandsAsync(TenantScope scope)
    {
        await using var tenant = await _fixture.Factory.OpenForTenantAsync(scope, CancellationToken.None);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.ev_discovery_commands WHERE project_id = @project;", tenant.Connection);
        command.Parameters.Add(new SqlParameter("@project", System.Data.SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> PostRequestDiscoveryAsync(HttpClient client, Guid environmentId, Guid idempotencyKey)
    {
        // O token antiforgery vem do formulário de logout presente no layout de qualquer página autenticada.
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
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.Found);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Token antiforgery não encontrado.");
        return match.Groups[1].Value;
    }

    private static string Flatten(Exception exception)
    {
        var builder = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            builder.Append(current.Message).Append(' ');
        }

        return builder.ToString();
    }
}
