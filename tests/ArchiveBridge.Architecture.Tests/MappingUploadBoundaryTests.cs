using System.Reflection;
using System.Text.RegularExpressions;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Passo 6A — o backend de validação de upload de mapping vive na Application e não pode arrastar nenhuma
/// dependência web nem de execução/fornecedor. Em particular, <c>ArchiveBridge.Application.Mapping</c> não
/// depende de ASP.NET Core (IFormFile/HttpContext), Razor, PowerShell (System.Management.Automation) nem de
/// qualquer assembly de fornecedor (Aspose/libpff/Graph/Purview/AzCopy/Azure Storage/EWS). Verificado por
/// reflexão sobre os assemblies referenciados.
/// </summary>
public sealed partial class MappingUploadBoundaryTests
{
    [GeneratedRegex(
        "microsoft\\.aspnetcore|\\.razor|razor\\.|system\\.management\\.automation|aspose|libpff|microsoft\\.graph|"
        + "purview|enterprisevault|symantec|veritas|azcopy|azure\\.storage|exchange\\.webservices",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenReferencePattern();

    [Fact]
    public void ApplicationAssemblyHasNoWebVendorOrExecutionDependency()
    {
        var forbidden = typeof(ArchiveBridge.Application.AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenReferencePattern().IsMatch(name))
            .ToList();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void ContractsAssemblyHasNoWebVendorOrExecutionDependency()
    {
        var forbidden = typeof(ArchiveBridge.Contracts.AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenReferencePattern().IsMatch(name))
            .ToList();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void InfrastructureAssemblyHasNoWebVendorOrExecutionDependency()
    {
        // A persistência (incluindo SqlMappingValidationStore, custódia da validação do Passo 6A) é adaptador
        // de saída: nunca conhece ASP.NET. A superfície HTTP do Passo 6B vive EXCLUSIVAMENTE no ControlPlane.
        var forbidden = typeof(ArchiveBridge.Infrastructure.AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenReferencePattern().IsMatch(name))
            .ToList();

        Assert.Empty(forbidden);
    }
}
