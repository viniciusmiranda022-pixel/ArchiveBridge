using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Workers.Ev;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Fronteiras arquiteturais do Worker EV operacional (sub-incrementos 2 e 2B):
/// (1) <see cref="EvDiscoveryWorker"/> delega SOMENTE ao processor da Application (nunca PowerShell/host/
/// caso de uso diretamente); (2) o host do worker (o novo caminho Request→Queue→Worker→Processor→Discovery)
/// não introduz nenhuma integração de Slice 4B nem pacotes de fornecedor; (3) a identidade de MANUTENÇÃO,
/// dentro do caminho ESPECÍFICO de descoberta EV (Infrastructure/EnterpriseVault), só aparece na enumeração
/// de escopos — a recuperação técnica de leases expirados é a OUTRA operação cross-tenant aprovada e reutiliza
/// o <c>SqlJobLeaseManager</c> do Slice 1 (Infrastructure/Jobs), não código EV específico.
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

    // (3) A fronteira de MANUTENÇÃO é restrita a operações técnicas cross-tenant APROVADAS. No caminho EV
    // específico (Infrastructure/EnterpriseVault), duas identidades de manutenção são aprovadas: a
    // ENUMERAÇÃO de escopos de descoberta (SqlEvDiscoveryPendingScopeReader) e o RESGATE de enrollment
    // tokens (SqlEnrollmentTokenStore.RedeemAsync, AB-4C-001) — o resgate precisa localizar o token
    // SOMENTE pelo hash do segredo apresentado, antes de o tenant/projeto serem conhecidos (é o PRÓPRIO
    // token quem os determina), então não há como abrir uma conexão tenant-scoped de antemão. A
    // recuperação de leases expirados (a outra operação aprovada fora deste diretório) vive no
    // SqlJobLeaseManager do Slice 1. Nenhum efeito de negócio EV (claim/discovery/evidência/conclusão/
    // capability/inventário) usa a identidade de manutenção além destes dois casos explicitamente aprovados.
    [Fact]
    public void MaintenanceIdentityIsRestrictedToApprovedCrossTenantInfrastructureOperations()
    {
        var usingMaintenance = Directory.EnumerateFiles(InfrastructureEvDir, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("OpenForMaintenanceAsync", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .Order(StringComparer.Ordinal)
            .ToList();

        string[] approved = ["SqlEnrollmentTokenStore.cs", "SqlEvDiscoveryPendingScopeReader.cs"];
        Assert.Equal(approved, usingMaintenance);
    }
}
