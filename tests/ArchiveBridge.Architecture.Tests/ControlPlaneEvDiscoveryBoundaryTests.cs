namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Fronteira arquitetural do write-path de descoberta EV no Control Plane (Stage 4). O Portal SOLICITA
/// (enfileira) descoberta e NADA MAIS: nunca executa descoberta inline no processo web. Prova que (1) nenhum
/// código do Control Plane referencia o processor/host/caso-de-uso de EXECUÇÃO; (2) o código do write-path
/// (/EnterpriseVault + o gate) não contém nenhum token de Slice 4B; (3) o Control Plane não declara pacote de
/// fornecedor algum; (4) conhece somente o caso de uso de SOLICITAÇÃO; (5) não depende do projeto Workers.Ev.
/// </summary>
public sealed class ControlPlaneEvDiscoveryBoundaryTests
{
    private static string ControlPlaneDir { get; } =
        Path.Combine(ProjectGraph.RepositoryRoot, "src", "ArchiveBridge.ControlPlane");

    // (1) Nenhuma EXECUÇÃO de descoberta no processo web: processor, caso de uso de execução, sonda PowerShell
    // ou host Windows NUNCA aparecem em qualquer código do Control Plane.
    [Theory]
    [InlineData("EvDiscoveryCommandProcessor")]
    [InlineData("DiscoverEvCapabilitiesUseCase")]
    [InlineData("PowerShellEvCapabilityDiscovery")]
    [InlineData("WindowsEvPowerShellHost")]
    [InlineData("ProcessNextAsync")]
    public void ControlPlaneNeverReferencesDiscoveryExecutionInternals(string forbidden)
    {
        foreach (var file in AllCodeFiles())
        {
            Assert.False(
                File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} referencia execução de descoberta proibida: {forbidden}");
        }
    }

    // (2) O CÓDIGO do write-path (/EnterpriseVault + o gate local) não contém token algum de Slice 4B. (Não se
    // escaneia o banner de Program.cs nem as views: eles contêm, por reasseguração de UX/documentação, a
    // afirmação de que tais capacidades NÃO são executadas — uma garantia textual, não uma integração.)
    [Theory]
    [InlineData("Export-EVArchive")]
    [InlineData("AzCopy")]
    [InlineData("Purview")]
    [InlineData("Microsoft.Graph")]
    [InlineData("Exchange.WebServices")]
    [InlineData("Exchange Online")]
    [InlineData("libpff")]
    [InlineData("Aspose")]
    [InlineData("PST")]
    public void WritePathCodeContainsNoSlice4bToken(string forbidden)
    {
        foreach (var file in WritePathCodeFiles())
        {
            Assert.False(
                File.ReadAllText(file).Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} (write-path) contém token proibido de Slice 4B: {forbidden}");
        }
    }

    // (3) Sinal AUTORITATIVO de "nenhuma integração": o Control Plane não declara nenhum pacote de fornecedor
    // (EV/PST/Purview/Graph/AzCopy/Azure Storage/Exchange).
    [Fact]
    public void ControlPlaneDeclaresNoVendorPackage()
    {
        string[] vendorTerms =
        [
            "aspose", "libpff", "microsoft.graph", "purview", "enterprisevault",
            "symantec", "veritas", "azcopy", "azure.storage", "exchange.webservices",
        ];

        var controlPlane = ProjectGraph.LoadSourceProjects().Single(project => project.Name == "ArchiveBridge.ControlPlane");
        foreach (var package in controlPlane.PackageReferences)
        {
            Assert.DoesNotContain(vendorTerms, term => package.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    // (4) O Control Plane conhece SOMENTE o caso de uso de SOLICITAÇÃO (enfileiramento durável idempotente).
    [Fact]
    public void ControlPlaneKnowsOnlyTheRequestUseCase()
    {
        var anyReferencesRequestUseCase = AllCodeFiles()
            .Any(file => File.ReadAllText(file).Contains("RequestEvCapabilityDiscoveryUseCase", StringComparison.Ordinal));
        Assert.True(anyReferencesRequestUseCase, "O Control Plane deve compor RequestEvCapabilityDiscoveryUseCase.");
    }

    // (5) Sem dependência de projeto ControlPlane → Workers.Ev (o gate é uma opção LOCAL do Control Plane).
    [Fact]
    public void ControlPlaneDoesNotReferenceTheEvWorkerProject()
    {
        var controlPlane = ProjectGraph.LoadSourceProjects().Single(project => project.Name == "ArchiveBridge.ControlPlane");
        Assert.DoesNotContain("ArchiveBridge.Workers.Ev", controlPlane.ProjectReferences);
    }

    private static IEnumerable<string> AllCodeFiles() =>
        Directory.EnumerateFiles(ControlPlaneDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // O código do write-path: o handler /EnterpriseVault e o gate LOCAL do Portal.
    private static IEnumerable<string> WritePathCodeFiles() =>
        AllCodeFiles().Where(path =>
            path.Contains($"{Path.DirectorySeparatorChar}EnterpriseVault{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.GetFileName(path) == "EnterpriseVaultDiscoveryPortalOptions.cs");
}
