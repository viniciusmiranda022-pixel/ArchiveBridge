namespace ArchiveBridge.Domain.Recovery;

/// <summary>
/// Medição REAL (início/fim observados de um exercício executado) de um objetivo de recuperação — nunca
/// um valor alegado sem execução. <see cref="Elapsed"/> é sempre derivado, nunca informado diretamente,
/// para que o valor persistido não possa divergir do intervalo realmente observado.
/// </summary>
public readonly record struct RecoveryObjectiveMeasurement
{
    /// <summary>Cria a medição a partir do início/fim reais do exercício.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completedAtUtc"/> é anterior a <paramref name="startedAtUtc"/>.</exception>
    public RecoveryObjectiveMeasurement(DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc)
    {
        if (completedAtUtc < startedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAtUtc), completedAtUtc, "O fim do exercício não pode ser anterior ao início.");
        }

        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    /// <summary>Instante real de início do exercício.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Instante real de conclusão do exercício.</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Duração observada — sempre derivada de início/fim, nunca um valor independente.</summary>
    public TimeSpan Elapsed => CompletedAtUtc - StartedAtUtc;
}
