using System.Reflection;
using System.Text.RegularExpressions;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Garante que nenhum tipo de fornecedor (Enterprise Vault, Purview, Graph, libpff, Aspose,
/// AzCopy/Azure Storage, EWS…) atravessa a fronteira do domínio: Domain e Contracts não podem
/// referenciar assemblies de fornecedor. Verificado por reflexão sobre os assemblies compilados.
/// </summary>
public sealed partial class VendorBoundaryTests
{
    [GeneratedRegex(
        "aspose|libpff|microsoft\\.graph|purview|enterprisevault|symantec|veritas|azcopy|azure\\.storage|" +
        "exchange\\.webservices|exchangeonlinemanagement|microsoft\\.exchange",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorAssemblyPattern();

    public static TheoryData<Assembly> BoundaryAssemblies() =>
        new()
        {
            typeof(ArchiveBridge.Domain.AssemblyMarker).Assembly,
            typeof(ArchiveBridge.Contracts.AssemblyMarker).Assembly,
        };

    [Theory]
    [MemberData(nameof(BoundaryAssemblies))]
    public void BoundaryAssemblyReferencesNoVendorAssembly(Assembly assembly)
    {
        var vendorReferences = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => VendorAssemblyPattern().IsMatch(name))
            .ToList();

        Assert.Empty(vendorReferences);
    }

    [Fact]
    public void DomainReferencesNoOtherArchiveBridgeAssembly()
    {
        var archiveBridgeReferences = typeof(ArchiveBridge.Domain.AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("ArchiveBridge.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(archiveBridgeReferences);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureAssembly()
    {
        var referenced = typeof(ArchiveBridge.Application.AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain("ArchiveBridge.Infrastructure", referenced);
    }

    // Estende a MESMA regra de fronteira (não cria uma verificação paralela) para os tipos de export
    // (AB-4C-005): nenhum arquivo-fonte de Domain/Application pode DEPENDER estruturalmente do
    // processo/EV concreto — a execução real (System.Diagnostics.Process, ProcessStartInfo, o snap-in
    // Symantec.EnterpriseVault.PowerShell) fica exclusivamente em Infrastructure/Workers.Ev. A checagem por
    // texto complementa as checagens por assembly acima porque System.Diagnostics.Process é BCL (nenhum
    // PackageReference apareceria) e por isso não seria pego pelas asserções de dependência de
    // pacote/assembly sozinhas. Não inclui "PowerShell"/"Export-EVArchive" soltos: esses termos já aparecem
    // legitimamente em comentários de domínio pré-existentes (ex.: ExportRequestPolicy, EvDiscoveryPolicy,
    // EvPowerShellProbeKind) ao DOCUMENTAR o cmdlet/host que o Domain nunca invoca — o sinal estrutural
    // preciso é o TIPO/namespace concreto, não a palavra em prosa.
    [Theory]
    [InlineData("System.Diagnostics.Process")]
    [InlineData("ProcessStartInfo")]
    [InlineData("Symantec.EnterpriseVault.PowerShell")]
    public void DomainAndApplicationSourceNeverReferencesProcessOrEvVendorTypes(string forbidden)
    {
        foreach (var file in DomainAndApplicationSourceFiles())
        {
            Assert.False(
                File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} (Domain/Application) contém o token proibido: {forbidden}");
        }
    }

    private static IEnumerable<string> DomainAndApplicationSourceFiles()
    {
        var root = ProjectGraph.RepositoryRoot;
        foreach (var project in new[] { "ArchiveBridge.Domain", "ArchiveBridge.Application" })
        {
            var dir = Path.Combine(root, "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }
}
