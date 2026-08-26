namespace ArchiveBridge.Domain.Performance;

/// <summary>Desfecho de UMA iteração de benchmark — nunca inferido, sempre o que o harness observou.</summary>
public enum BenchmarkIterationOutcome
{
    /// <summary>A iteração completou normalmente.</summary>
    Success,

    /// <summary>A iteração lançou uma exceção não relacionada a cancelamento/limite de recurso.</summary>
    Error,

    /// <summary>A iteração foi cancelada (token de cancelamento do chamador).</summary>
    Cancelled,

    /// <summary>A iteração encerrou por limite de recurso (ex.: espaço/tamanho excedido pelo próprio caminho medido).</summary>
    ResourceLimit,
}

/// <summary>
/// Medição de UMA iteração, sanitizada por construção: somente campos numéricos/enum — nunca mensagem de
/// exceção, caminho ou qualquer texto livre que pudesse carregar PII (AB-I7-003 §9).
/// </summary>
public sealed record BenchmarkMeasurement
{
    /// <summary>Cria a medição de uma iteração, validando não-negatividade e finitude dos campos numéricos.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Índice/valores negativos, ou valor não finito.</exception>
    public BenchmarkMeasurement(
        int iterationIndex,
        double wallClockMs,
        double? cpuTimeMs,
        long? peakWorkingSetBytes,
        long? bytesProcessed,
        long? itemsProcessed,
        BenchmarkIterationOutcome outcome)
    {
        if (iterationIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterationIndex), "O índice da iteração não pode ser negativo.");
        }

        if (!double.IsFinite(wallClockMs) || wallClockMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wallClockMs), "wallClockMs precisa ser finito e não-negativo.");
        }

        if (cpuTimeMs is not null && (!double.IsFinite(cpuTimeMs.Value) || cpuTimeMs.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cpuTimeMs), "cpuTimeMs, quando presente, precisa ser finito e não-negativo.");
        }

        if (peakWorkingSetBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(peakWorkingSetBytes), "peakWorkingSetBytes não pode ser negativo.");
        }

        if (bytesProcessed is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesProcessed), "bytesProcessed não pode ser negativo.");
        }

        if (itemsProcessed is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemsProcessed), "itemsProcessed não pode ser negativo.");
        }

        IterationIndex = iterationIndex;
        WallClockMs = wallClockMs;
        CpuTimeMs = cpuTimeMs;
        PeakWorkingSetBytes = peakWorkingSetBytes;
        BytesProcessed = bytesProcessed;
        ItemsProcessed = itemsProcessed;
        Outcome = outcome;
    }

    /// <summary>Índice da iteração (0-based) — nunca inclui as iterações de warmup.</summary>
    public int IterationIndex { get; }

    /// <summary>Duração observada (wall-clock), em milissegundos.</summary>
    public double WallClockMs { get; }

    /// <summary>Tempo de CPU do processo consumido durante a iteração, quando disponível.</summary>
    public double? CpuTimeMs { get; }

    /// <summary>Peak working set do processo no momento em que a iteração terminou, quando disponível.</summary>
    public long? PeakWorkingSetBytes { get; }

    /// <summary>Bytes processados na iteração, quando semanticamente aplicável.</summary>
    public long? BytesProcessed { get; }

    /// <summary>Itens processados na iteração, quando semanticamente aplicável.</summary>
    public long? ItemsProcessed { get; }

    /// <summary>Desfecho observado da iteração.</summary>
    public BenchmarkIterationOutcome Outcome { get; }

    /// <summary>Throughput em bytes/s desta iteração, quando <see cref="BytesProcessed"/> e duração positiva estão disponíveis.</summary>
    public double? BytesPerSecond =>
        BytesProcessed is { } bytes && WallClockMs > 0 ? bytes / (WallClockMs / 1000d) : null;

    /// <summary>Throughput em itens/s desta iteração, quando <see cref="ItemsProcessed"/> e duração positiva estão disponíveis.</summary>
    public double? ItemsPerSecond =>
        ItemsProcessed is { } items && WallClockMs > 0 ? items / (WallClockMs / 1000d) : null;
}
