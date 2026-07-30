namespace ArchiveBridge.Domain.Waves;

/// <summary>Lançada quando uma transição de estado de onda não permitida é tentada (fail-closed).</summary>
public sealed class InvalidWaveTransitionException : Exception
{
    /// <summary>Cria a exceção sem mensagem.</summary>
    public InvalidWaveTransitionException()
    {
    }

    /// <summary>Cria a exceção com mensagem.</summary>
    public InvalidWaveTransitionException(string message)
        : base(message)
    {
    }

    /// <summary>Cria a exceção com mensagem e causa.</summary>
    public InvalidWaveTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Fábrica para uma transição específica não permitida.</summary>
    public static InvalidWaveTransitionException Between(WaveStatus from, WaveStatus to) =>
        new($"Transição de onda inválida de {from} para {to}.");
}

/// <summary>Transições permitidas do ciclo de vida de uma onda; falha fechada para as demais.</summary>
public static class WaveStateMachine
{
    private static readonly Dictionary<WaveStatus, IReadOnlySet<WaveStatus>> AllowedTargets = new()
    {
        [WaveStatus.Draft] = new HashSet<WaveStatus> { WaveStatus.Validating, WaveStatus.Cancelled },
        [WaveStatus.Validating] = new HashSet<WaveStatus>
        {
            WaveStatus.Blocked,
            WaveStatus.ReadyForApproval,
            WaveStatus.Draft,
            WaveStatus.Cancelled,
        },
        [WaveStatus.Blocked] = new HashSet<WaveStatus>
        {
            WaveStatus.Validating,
            WaveStatus.Draft,
            WaveStatus.Cancelled,
        },
        [WaveStatus.ReadyForApproval] = new HashSet<WaveStatus>
        {
            WaveStatus.Approved,
            WaveStatus.Draft,
            WaveStatus.Cancelled,
        },
        [WaveStatus.Approved] = new HashSet<WaveStatus> { WaveStatus.Frozen, WaveStatus.Cancelled },
        [WaveStatus.Frozen] = new HashSet<WaveStatus> { WaveStatus.Completed, WaveStatus.Cancelled },
        [WaveStatus.Completed] = new HashSet<WaveStatus>(),
        [WaveStatus.Cancelled] = new HashSet<WaveStatus>(),
    };

    /// <summary>Estados terminais.</summary>
    public static bool IsTerminal(WaveStatus status) =>
        status is WaveStatus.Completed or WaveStatus.Cancelled;

    /// <summary>Seleção e destino mutáveis (antes da aprovação).</summary>
    public static bool IsSelectionMutable(WaveStatus status) =>
        status is WaveStatus.Draft or WaveStatus.Validating or WaveStatus.Blocked or WaveStatus.ReadyForApproval;

    /// <summary>Indica se a transição é permitida.</summary>
    public static bool CanTransition(WaveStatus from, WaveStatus to) =>
        AllowedTargets.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Garante a transição; lança <see cref="InvalidWaveTransitionException"/> se inválida.</summary>
    public static void EnsureCanTransition(WaveStatus from, WaveStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw InvalidWaveTransitionException.Between(from, to);
        }
    }
}
