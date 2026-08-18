namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// O Modo de Demonstração (Presentation Mode) é um concern EXCLUSIVO DE UI: vive somente no
/// <c>ArchiveBridge.ControlPlane</c>. Domain, Application, Infrastructure e Contracts NÃO podem conhecê-lo —
/// nenhuma regra de negócio, store ou porta referencia o provedor sintético, as opções do modo ou o
/// namespace de apresentação. Verificado por varredura dos fontes (fail-closed).
/// </summary>
public sealed class PresentationModeBoundaryTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "IPresentationDataProvider",
        "SyntheticPresentationDataProvider",
        "PresentationModeOptions",
        "ControlPlane.Presentation",
    ];

    [Theory]
    [InlineData("ArchiveBridge.Domain")]
    [InlineData("ArchiveBridge.Application")]
    [InlineData("ArchiveBridge.Infrastructure")]
    [InlineData("ArchiveBridge.Contracts")]
    public void PresentationModeIsNotReferencedOutsideControlPlane(string project)
    {
        var projectDir = Path.Combine(ProjectGraph.RepositoryRoot, "src", project);
        var files = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in ForbiddenTokens)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    $"{project}/{Path.GetFileName(file)} referencia '{token}': o Presentation Mode deve viver apenas no ControlPlane.");
            }
        }
    }
}
