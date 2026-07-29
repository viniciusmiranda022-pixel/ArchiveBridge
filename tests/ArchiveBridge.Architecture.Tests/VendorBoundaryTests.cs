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
        "aspose|libpff|microsoft\\.graph|purview|enterprisevault|symantec|veritas|azcopy|azure\\.storage|exchange\\.webservices",
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
}
