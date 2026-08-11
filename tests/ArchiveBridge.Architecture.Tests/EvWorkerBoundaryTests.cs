using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Workers.Ev;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Fronteiras arquiteturais do Worker EV operacional (sub-incremento 2):
/// (1) <see cref="EvDiscoveryWorker"/> delega SOMENTE ao processor da Application (nunca PowerShell/host/
/// caso de uso diretamente); (2) o host do worker (o novo caminho Request→Queue→Worker→Processor→Discovery)
/// não introduz nenhuma integração de Slice 4B nem pacotes de fornecedor; (3) a identidade de MANUTENÇÃO só
/// aparece no leitor de escopos dentro do caminho de descoberta EV.
/// </summary>
public sealed class EvWorkerBoundaryTests
{
    private static string WorkerHostDir { get; } =
        Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Workers.Ev");

    private static string InfrastructureEvDir { get; } =
        Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.Infrastructure", "EnterpriseVault");

    // (1) O worker só chama EvDiscoveryCommandProcessor.ProcessNextAsync — sem execução inline.
    [Fact]
    public void EvDiscoveryWorkerDelegatesOnlyToProcessNextAsync()
    {
        var source = File.ReadAllText(Path.Combine(WorkerHostDir, "EvDiscoveryWorker.cs"));

        Assert.True(source.Contains("ProcessNextAsync", StringComparison.Ordinal));
        Assert.False(source.Contains("PowerShellEvCapabilityDiscovery", StringComparison.Ordinal));
        Assert.False(source.Contains("WindowsEvPowerShellHost", StringComparison.Ordinal));
        Assert.False(source.Contains("DiscoverEvCapabilitiesUseCase", StringComparison.Ordinal));
        Assert.False(source.Contains(".ExecuteAsync(", StringComparison.Ordinal)); // nunca executa o caso de uso inline
    }

    // (1') Confirmação por reflexão: o worker depende do PROCESSOR, nunca das implementações concretas de
    // sonda/host nem do caso de uso de descoberta.
    [Fact]
    public void EvDiscoveryWorkerDependsOnProcessorNotOnDiscoveryInternals()
    {
        var parameters = typeof(EvDiscoveryWorker).GetConstructors().Single()
            .GetParameters().Select(parameter => parameter.ParameterType).ToList();

        Assert.Contains(typeof(EvDiscoveryCommandProcessor), parameters);
        Assert.DoesNotContain(typeof(DiscoverEvCapabilitiesUseCase), parameters);
        Assert.DoesNotContain(typeof(PowerShellEvCapabilityDiscovery), parameters);
        Assert.DoesNotContain(typeof(WindowsEvPowerShellHost), parameters);
    }

    // (2) O host do worker não introduz nenhuma integração/execução de Slice 4B.
    [Theory]
    [InlineData("Export-EVArchive")]
    [InlineData("AzCopy")]
    [InlineData("Purview")]
    [InlineData("Microsoft.Graph")]
    [InlineData("Exchange.WebServices")]
    [InlineData("Exchange Online")]
    [InlineData("libpff")]
    [InlineData("Aspose")]
    public void WorkerHostIntroducesNoSlice4bIntegration(string forbidden)
    {
        foreach (var file in Directory.EnumerateFiles(WorkerHostDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                source.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} contém o token proibido de Slice 4B: {forbidden}");
        }
    }

    // (2') O host do worker não declara nenhum pacote de fornecedor (nada de EV/PST/Purview/Graph/AzCopy).
    [Fact]
    public void WorkerHostDeclaresNoVendorPackage()
    {
        string[] vendorTerms =
        [
            "aspose", "libpff", "microsoft.graph", "purview", "enterprisevault",
            "symantec", "veritas", "azcopy", "azure.storage", "exchange.webservices",
        ];

        var worker = ProjectGraph.LoadSourceProjects().Single(project => project.Name == "ArchiveBridge.Workers.Ev");
        foreach (var package in worker.PackageReferences)
        {
            Assert.DoesNotContain(vendorTerms, term => package.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    // (3) A identidade de manutenção é confinada ao leitor de escopos dentro do caminho de descoberta EV.
    [Fact]
    public void MaintenanceIdentityIsConfinedToThePendingScopeReader()
    {
        var usingMaintenance = Directory.EnumerateFiles(InfrastructureEvDir, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("OpenForMaintenanceAsync", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.Equal("SqlEvDiscoveryPendingScopeReader.cs", Assert.Single(usingMaintenance));
    }
}
