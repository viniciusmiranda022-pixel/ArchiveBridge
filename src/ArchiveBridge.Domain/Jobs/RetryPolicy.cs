namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Política de retry com backoff exponencial limitado. Value object determinístico (dado o
/// mesmo <c>attemptCount</c> e instante, produz o mesmo agendamento) — testável em unidade.
/// </summary>
public readonly record struct RetryPolicy(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    /// <summary>Política padrão: até 5 tentativas, backoff base de 30s, teto de 15min.</summary>
    public static RetryPolicy Default => new(5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

    /// <summary>Indica se ainda há tentativas disponíveis após <paramref name="attemptCount"/> tentativas.</summary>
    public bool ShouldRetry(int attemptCount) => attemptCount < MaxAttempts;

    /// <summary>
    /// Calcula o próximo instante elegível: <c>BaseDelay * 2^(attemptCount-1)</c>, limitado a
    /// <see cref="MaxDelay"/>. Determinístico e sem jitter (o jitter, se necessário, entra por slice futuro).
    /// </summary>
    public DateTimeOffset NextAttempt(int attemptCount, DateTimeOffset now)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 20);
        var factor = Math.Pow(2, exponent);
        var delayTicks = BaseDelay.Ticks * factor;
        var cappedTicks = (long)Math.Min(delayTicks, MaxDelay.Ticks);
        return now + TimeSpan.FromTicks(cappedTicks);
    }
}
