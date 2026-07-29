using System.Xml.Linq;

namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Lê o grafo de projetos (.csproj) do repositório em disco para asserções estruturais
/// deterministas — independentemente de o compilador C# ter elidido referências não usadas.
/// A regra de dependência é verificada no grafo declarado, não apenas nos assemblies compilados.
/// </summary>
internal sealed class ProjectGraph
{
    private ProjectGraph(string name, IReadOnlyList<string> projectReferences, IReadOnlyList<string> packageReferences)
    {
        Name = name;
        ProjectReferences = projectReferences;
        PackageReferences = packageReferences;
    }

    /// <summary>Nome simples do projeto (sem extensão), ex.: <c>ArchiveBridge.Domain</c>.</summary>
    public string Name { get; }

    /// <summary>Nomes simples dos projetos referenciados via <c>ProjectReference</c>.</summary>
    public IReadOnlyList<string> ProjectReferences { get; }

    /// <summary>Ids dos pacotes referenciados via <c>PackageReference</c>.</summary>
    public IReadOnlyList<string> PackageReferences { get; }

    /// <summary>Raiz do repositório, localizada pela presença de <c>ArchiveBridge.sln</c>.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Carrega todos os projetos sob <c>src/</c>.</summary>
    public static IReadOnlyList<ProjectGraph> LoadSourceProjects()
    {
        var sourceDir = Path.Combine(RepositoryRoot, "src");
        var projects = new List<ProjectGraph>();
        foreach (var csproj in Directory.EnumerateFiles(sourceDir, "*.csproj", SearchOption.AllDirectories))
        {
            projects.Add(Load(csproj));
        }

        return projects;
    }

    private static ProjectGraph Load(string csprojPath)
    {
        var document = XDocument.Load(csprojPath);

        var projectReferences = document.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar)))
            .ToList();

        var packageReferences = document.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => include!)
            .ToList();

        return new ProjectGraph(Path.GetFileNameWithoutExtension(csprojPath), projectReferences, packageReferences);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArchiveBridge.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Não foi possível localizar a raiz do repositório (ArchiveBridge.sln) a partir de " + AppContext.BaseDirectory);
    }
}
