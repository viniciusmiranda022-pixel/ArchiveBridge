namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Prioridade de reivindicação, restrita ao intervalo fechado [<see cref="MinValue"/>,
/// <see cref="MaxValue"/>]. Valor maior é reivindicado antes; a ordenação de claim usa a
/// <b>prioridade efetiva</b> = prioridade + (idade em segundos ÷ agingInterval), o que garante
/// aging: um Job antigo de baixa prioridade ultrapassa Jobs novos de alta prioridade.
/// O intervalo limitado, combinado ao agingInterval, define um <b>limite máximo de espera</b> =
/// (<see cref="MaxValue"/> − <see cref="MinValue"/>) × agingInterval.
/// </summary>
public readonly record struct JobPriority
{
    /// <summary>Menor prioridade válida (a mais baixa).</summary>
    public const int MinValue = 0;

    /// <summary>Maior prioridade válida (a mais alta).</summary>
    public const int MaxValue = 100;

    /// <summary>Cria uma prioridade dentro do intervalo permitido.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Quando <paramref name="value"/> está fora de [0, 100].</exception>
    public JobPriority(int value)
    {
        if (value is < MinValue or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"JobPriority deve estar no intervalo [{MinValue}, {MaxValue}].");
        }

        Value = value;
    }

    /// <summary>Valor numérico da prioridade.</summary>
    public int Value { get; }

    /// <summary>Prioridade padrão (a mais baixa).</summary>
    public static JobPriority Normal => new(MinValue);

    /// <summary>Prioridade máxima.</summary>
    public static JobPriority Highest => new(MaxValue);
}
