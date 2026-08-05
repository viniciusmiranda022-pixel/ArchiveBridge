using System.Net;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A — o Portal em nível HTTP (host real via <see cref="WebApplicationFactory{TEntryPoint}"/> ligado ao
/// SQL Server de teste). Prova, sem simular: o host sobe (<c>/health/live</c>); a autenticação é FAIL-CLOSED
/// (páginas protegidas redirecionam ao login); o login por formulário funciona de ponta a ponta (com
/// antiforgery); e o RBAC nega uma área de Administrador a um usuário Auditor. Nenhuma capacidade do Slice 4B
/// é exercida.
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
            builder.UseSetting("ConnectionStrings:Application", fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // banco de teste disponível ⇒ pronto
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

        // Página de leitura autorizada para qualquer papel autenticado.
        using var jobs = await client.GetAsync(new Uri("/Jobs/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.Contains("Jobs duráveis", await jobs.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Área de Administrador negada a um Auditor (redireciona ao AccessDenied).
        using var admin = await client.GetAsync(new Uri("/Admin/Index", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode); // seguiu o redirect para /Account/Denied
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
        // Não emitiu cookie de autenticação nem redirecionou para uma página protegida.
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);

        // Prova definitiva de não-autenticação: uma página protegida ainda exige login.
        using var protectedResponse = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Found, protectedResponse.StatusCode);
        Assert.Contains("/Account/Login", protectedResponse.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        using var content = FormContent(username, password, token);
        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // seguiu o redirect para o painel autenticado
        Assert.Contains("Painel operacional", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
