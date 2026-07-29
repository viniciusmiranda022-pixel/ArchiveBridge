namespace ArchiveBridge.Architecture.Tests;

/// <summary>
/// Regras de dependência da arquitetura hexagonal, verificadas no grafo de <c>ProjectReference</c>
/// declarado nos <c>.csproj</c> (determinista; independe de elisão do compilador).
/// </summary>
public sealed class DependencyRuleTests
{
    private const string Domain = "ArchiveBridge.Domain";
    private const string Contracts = "ArchiveBridge.Contracts";
    private const string Application = "ArchiveBridge.Application";
    private const string Infrastructure = "ArchiveBridge.Infrastructure";
    private const string ControlPlane = "ArchiveBridge.ControlPlane";
    private const string WorkerPrefix = "ArchiveBridge.Workers.";

    private static IReadOnlyList<ProjectGraph> Projects { get; } = ProjectGraph.LoadSourceProjects();

    private static ProjectGraph Project(string name) => Projects.Single(project => project.Name == name);

    [Fact]
    public void DomainDependsOnNothing()
    {
        Assert.Empty(Project(Domain).ProjectReferences);
    }

    [Fact]
    public void DomainDeclaresNoPackageReference()
    {
        // O núcleo de domínio não pode depender de nenhum pacote NuGet (nenhum SDK de fornecedor).
        Assert.Empty(Project(Domain).PackageReferences);
    }

    [Fact]
    public void ContractsDeclaresNoPackageReference()
    {
        // Contracts define apenas portas/DTOs sobre o domínio — sem pacotes de fornecedor.
        Assert.Empty(Project(Contracts).PackageReferences);
    }

    [Fact]
    public void ContractsDependsOnlyOnDomain()
    {
        Assert.Single(Project(Contracts).ProjectReferences);
        Assert.Contains(Domain, Project(Contracts).ProjectReferences);
    }

    [Fact]
    public void ContractsDoesNotDependOnInfrastructure()
    {
        Assert.DoesNotContain(Infrastructure, Project(Contracts).ProjectReferences);
    }

    [Fact]
    public void ApplicationDependsOnlyOnDomainAndContracts()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { Domain, Contracts };
        Assert.All(Project(Application).ProjectReferences, reference => Assert.Contains(reference, allowed));
    }

    [Fact]
    public void ApplicationDoesNotDependOnAConcreteImplementation()
    {
        // Application orquestra por portas (Contracts); nunca referencia a Infrastructure concreta.
        Assert.DoesNotContain(Infrastructure, Project(Application).ProjectReferences);
    }

    [Fact]
    public void InfrastructureDependsOnlyOnDomainAndContracts()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { Domain, Contracts };
        Assert.All(Project(Infrastructure).ProjectReferences, reference => Assert.Contains(reference, allowed));
    }

    [Fact]
    public void NoWorkerDependsOnAnotherWorker()
    {
        var workers = Projects
            .Where(project => project.Name.StartsWith(WorkerPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(workers);

        foreach (var worker in workers)
        {
            var otherWorkerReferences = worker.ProjectReferences
                .Where(reference => reference.StartsWith(WorkerPrefix, StringComparison.Ordinal)
                    && !string.Equals(reference, worker.Name, StringComparison.Ordinal))
                .ToList();

            Assert.Empty(otherWorkerReferences);
        }
    }

    [Fact]
    public void NoProjectDependsOnAHost()
    {
        // Os hosts (ControlPlane e workers) são raízes de composição: ninguém depende deles.
        var hosts = Projects
            .Where(project => string.Equals(project.Name, ControlPlane, StringComparison.Ordinal)
                || project.Name.StartsWith(WorkerPrefix, StringComparison.Ordinal))
            .Select(project => project.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var project in Projects)
        {
            var hostReferences = project.ProjectReferences.Where(hosts.Contains).ToList();
            Assert.Empty(hostReferences);
        }
    }
}
