using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// Slice 4A, Passo 8 — prova o lado "indisponível" de <c>/health/ready</c> (AB-8-001, itens 3 e 5): readiness
/// NUNCA declara pronto quando o SQL Server obrigatório está inacessível. Não depende do <see
/// cref="Support.SqlServerFixture"/> — deliberadamente aponta a string de conexão da APLICAÇÃO para um
/// endpoint que recusa conexão, sem tocar SQL Server real algum.
/// </summary>
public sealed class Slice4aHealthReadyUnavailableTests
{
    [Fact]
    public async Task HealthReadyReturns503WhenDatabaseIsUnreachable()
    {
        // Porta alta improvável de ter um listener local; timeout curto para a falha ser rápida e determinística
        // (conexão recusada, não timeout de rede — não depende de latência do ambiente de CI).
        const string unreachableConnection =
            "Server=127.0.0.1,58991;Database=doesnotmatter;User ID=sa;Password=unreachable;" +
            "TrustServerCertificate=True;Encrypt=False;Connect Timeout=2";

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", unreachableConnection);
            // Nunca aberta neste teste (readiness só toca a conexão da Application), mas obrigatória no startup.
            builder.UseSetting("ConnectionStrings:Maintenance", unreachableConnection);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
        });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthLiveStaysOkEvenWhenDatabaseIsUnreachable()
    {
        // Liveness prova só que o host está no ar — nunca depende do SQL Server. Distinto de readiness.
        const string unreachableConnection =
            "Server=127.0.0.1,58992;Database=doesnotmatter;User ID=sa;Password=unreachable;" +
            "TrustServerCertificate=True;Encrypt=False;Connect Timeout=2";

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Development");
            builder.UseSetting("ConnectionStrings:Application", unreachableConnection);
            builder.UseSetting("ConnectionStrings:Maintenance", unreachableConnection);
            builder.UseSetting("ControlPlane:RunMigrationsAtStartup", "false");
            builder.UseSetting("ControlPlane:BootstrapAdmin:Password", string.Empty);
        });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
