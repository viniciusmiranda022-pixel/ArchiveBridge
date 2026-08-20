using System.Net;
using System.Text.RegularExpressions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Infrastructure.ControlPlane;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A, Passo 8 — rate limiting das operações sensíveis JÁ EXISTENTES (AB-8-001, item 2). Prova que
/// estourar o limite responde <c>429</c> tanto no login (partição por endereço remoto, anti-força-bruta)
/// quanto em um write-path autenticado (partição por usuário). Nenhum novo write-path é exercitado — apenas
/// os handlers POST já cobertos pelos demais testes de HTTP do Slice 4A.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice4aRateLimitingTests(SqlServerFixture fixture)
{
    private static readonly Pbkdf2PasswordHasher Hasher = new();
    private readonly SqlServerFixture _fixture = fixture;

    [Fact]
    public async Task LoginIsRateLimitedAfterRepeatedAttemptsFromTheSameClient()
    {
        using var factory = CreateFactory(discoveryEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Política "login": 5 tentativas/minuto por endereço remoto (o TestServer usa 127.0.0.1 para todas as
        // requisições do mesmo client). As 5 primeiras são processadas normalmente (credenciais inválidas ⇒
        // não-302); a 6ª estoura o limite ⇒ 429, ANTES de tocar o handler/credenciais.
        HttpStatusCode? lastStatus = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("Username", "nao-existe"),
                new KeyValuePair<string, string>("Password", "senha-errada"),
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
            ]);
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
            lastStatus = response.StatusCode;

            if (attempt <= 5)
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
    }

    [Fact]
    public async Task SensitiveWriteEndpointIsRateLimitedAfterRepeatedRequestsFromTheSameUser()
    {
        var scope = SqlServerFixture.NewScope();
        var (username, password) = await SeedUserAsync(scope, PortalRoles.Operator);

        using var factory = CreateFactory(discoveryEnabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await LoginAsync(client, username, password);

        // Política "sensitive-write": 20 requisições/minuto por usuário autenticado. O ambiente não existe
        // (404 anti-IDOR em cada tentativa) — irrelevante para o rate limiter, que conta toda invocação do
        // handler, independentemente do resultado de negócio. A 21ª requisição estoura o limite ⇒ 429.
        HttpStatusCode? lastStatus = null;
        for (var attempt = 1; attempt <= 21; attempt++)
        {
            using var response = await PostRequestDiscoveryAsync(client, Guid.NewGuid(), Guid.NewGuid());
            lastStatus = response.StatusCode;

            if (attempt <= 20)
            {
                Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
    }

    private WebApplicationFactory<Program> CreateFactory(bool discoveryEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", _fixture.ConnectionString);
            builder.UseSetting("ConnectionStrings:Maintenance", _fixture.MaintenanceConnectionString);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:EvidenceRoot", _fixture.ArtifactRoot);
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
            builder.UseSetting("EnterpriseVaultDiscovery:Enabled", discoveryEnabled ? "true" : "false");
        });

    private async Task<(string Username, string Password)> SeedUserAsync(TenantScope scope, string role)
    {
        var username = "ratelimit_" + Guid.NewGuid().ToString("N");
        const string password = "R@teL1m1t!pw";
        await new SqlPortalUserStore(_fixture.ConnectionString).CreateAsync(
            new PortalUserRegistration(username, "Rate Limit Test", scope.Tenant, scope.Project, Hasher.Hash(password), [role]),
            CancellationToken.None);
        return (username, password);
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

    private static async Task<HttpResponseMessage> PostRequestDiscoveryAsync(HttpClient client, Guid environmentId, Guid idempotencyKey)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/EnterpriseVault/Index");
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("EnvironmentId", environmentId.ToString("D")),
            new KeyValuePair<string, string>("IdempotencyKey", idempotencyKey.ToString("D")),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]);
        return await client.PostAsync(new Uri("/EnterpriseVault/Index?handler=RequestDiscovery", UriKind.Relative), content);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Token antiforgery não encontrado no formulário.");
        return match.Groups[1].Value;
    }
}
