namespace ArchiveBridge.Domain.Projects;

/// <summary>Lançada quando uma transição de estado de projeto não permitida é tentada (fail-closed).</summary>
public sealed class InvalidProjectTransitionException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public InvalidProjectTransitionException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public InvalidProjectTransitionException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public InvalidProjectTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Fábrica para uma transição específica não permitida.</summary>
    public static InvalidProjectTransitionException Between(ProjectStatus from, ProjectStatus to) =>
        new($"Transição de projeto inválida de {from} para {to}.");
}

/// <summary>Transições permitidas do ciclo de vida de um projeto; falha fechada para as demais.</summary>
public static class ProjectStateMachine
{
    private static readonly Dictionary<ProjectStatus, IReadOnlySet<ProjectStatus>> AllowedTargets = new()
    {
        [ProjectStatus.Draft] = new HashSet<ProjectStatus> { ProjectStatus.ReadyForAssessment, ProjectStatus.Cancelled },
        [ProjectStatus.ReadyForAssessment] = new HashSet<ProjectStatus>
        {
            ProjectStatus.Blocked,
            ProjectStatus.Approved,
            ProjectStatus.Draft,
            ProjectStatus.Cancelled,
        },
        [ProjectStatus.Blocked] = new HashSet<ProjectStatus>
        {
            ProjectStatus.ReadyForAssessment,
            ProjectStatus.Draft,
            ProjectStatus.Cancelled,
        },
        [ProjectStatus.Approved] = new HashSet<ProjectStatus> { ProjectStatus.Active, ProjectStatus.Cancelled },
        [ProjectStatus.Active] = new HashSet<ProjectStatus> { ProjectStatus.Completed, ProjectStatus.Cancelled },
        [ProjectStatus.Completed] = new HashSet<ProjectStatus>(),
        [ProjectStatus.Cancelled] = new HashSet<ProjectStatus>(),
    };

    /// <summary>Estados terminais.</summary>
    public static bool IsTerminal(ProjectStatus status) =>
        status is ProjectStatus.Completed or ProjectStatus.Cancelled;

    /// <summary>Configuração mutável (permite alterar config/nome/owner e criar novas versões).</summary>
    public static bool IsConfigurationMutable(ProjectStatus status) =>
        status is ProjectStatus.Draft or ProjectStatus.ReadyForAssessment or ProjectStatus.Blocked;

    /// <summary>Indica se a transição é permitida.</summary>
    public static bool CanTransition(ProjectStatus from, ProjectStatus to) =>
        AllowedTargets.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Garante a transição; lança <see cref="InvalidProjectTransitionException"/> se inválida.</summary>
    public static void EnsureCanTransition(ProjectStatus from, ProjectStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw InvalidProjectTransitionException.Between(from, to);
        }
    }
}
