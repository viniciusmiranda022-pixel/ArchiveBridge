namespace ArchiveBridge.Domain.Jobs;

/// <summary>Lançada quando uma transição de estado não permitida é tentada (fail-closed).</summary>
public sealed class InvalidJobTransitionException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public InvalidJobTransitionException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public InvalidJobTransitionException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa interna.</summary>
    public InvalidJobTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Fábrica para uma transição específica não permitida.</summary>
    public static InvalidJobTransitionException Between(JobState from, JobState to) =>
        new($"Transição inválida de {from} para {to}.");
}

/// <summary>
/// Máquina de estados dos Jobs: define as transições permitidas e falha de forma fechada
/// para qualquer outra. É a autoridade das regras, espelhada pelos comandos SQL atômicos.
/// </summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobState, IReadOnlySet<JobState>> AllowedTargets =
        new()
        {
            [JobState.Pending] = new HashSet<JobState> { JobState.Processing, JobState.Cancelled },
            [JobState.Processing] = new HashSet<JobState>
            {
                JobState.Completed,
                JobState.Failed,
                JobState.RetryScheduled,
                JobState.Cancelled,
            },
            [JobState.RetryScheduled] = new HashSet<JobState>
            {
                JobState.Processing,
                JobState.Failed,
                JobState.Cancelled,
            },
            [JobState.Completed] = new HashSet<JobState>(),
            [JobState.Failed] = new HashSet<JobState>(),
            [JobState.Cancelled] = new HashSet<JobState>(),
        };

    /// <summary>Estados terminais não admitem novas transições.</summary>
    public static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Cancelled;

    /// <summary>Indica se a transição <paramref name="from"/> → <paramref name="to"/> é permitida.</summary>
    public static bool CanTransition(JobState from, JobState to) =>
        AllowedTargets.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Garante a transição; lança <see cref="InvalidJobTransitionException"/> se inválida.</summary>
    public static void EnsureCanTransition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
        {
            throw InvalidJobTransitionException.Between(from, to);
        }
    }
}
