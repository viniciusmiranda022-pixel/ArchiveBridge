using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Application.Performance;

/// <summary>Delta determinístico de UMA métrica agregada entre um baseline e uma execução atual.</summary>
public sealed record PerformanceRegressionMetricDelta(
    string MetricName, double BaselineValue, double CurrentValue, double AbsoluteDelta, double PercentDelta);

/// <summary>
/// Relatório de comparação entre duas execuções do MESMO cenário. Nunca carrega um veredito de
/// aprovação/reprovação — apenas os deltas e um aviso explícito de que são informativos (AB-I7-003 §6).
/// </summary>
public sealed record PerformanceRegressionReport(
    string ScenarioName,
    PerformanceBenchmarkRunId BaselineRunId,
    PerformanceBenchmarkRunId CurrentRunId,
    IReadOnlyList<PerformanceRegressionMetricDelta> MetricDeltas,
    string Notice);

/// <summary>
/// Compara duas execuções de benchmark do MESMO cenário e produz um relatório determinístico de deltas
/// (AB-I7-003 §6). NUNCA inventa um threshold de aprovação/regressão: não existe, hoje, nenhum critério de
/// regressão versionado e aprovado no repositório — o resultado é sempre evidência informativa, nunca um
/// veredito que promove ou falha automaticamente um CI. Quando um critério versionado existir no futuro,
/// ele deve ser aplicado por cima deste relatório (fora deste tipo), nunca embutido aqui como valor mágico.
/// </summary>
public static class PerformanceRegressionComparer
{
    /// <summary>Aviso fixo anexado a todo relatório — nenhum caminho de código pode omiti-lo.</summary>
    public const string InformativeOnlyNotice =
        "Delta informativo — nenhum critério de regressão versionado foi fornecido; não promove nem falha automaticamente (AB-I7-003 §6).";

    /// <summary>Compara <paramref name="baseline"/> contra <paramref name="current"/> (mesmo <see cref="PerformanceBenchmarkRunRecord.ScenarioName"/>).</summary>
    /// <exception cref="ArgumentException">Cenários diferentes.</exception>
    public static PerformanceRegressionReport Compare(PerformanceBenchmarkRunRecord baseline, PerformanceBenchmarkRunRecord current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        if (!string.Equals(baseline.ScenarioName, current.ScenarioName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Baseline e execução atual precisam ser do mesmo cenário (ScenarioName divergente).", nameof(current));
        }

        // Ordem FIXA e determinística de construção (nunca dependente de iteração de dicionário/hash-set):
        // a mesma dupla (baseline, current) produz sempre a MESMA lista de deltas, na MESMA ordem.
        var deltas = new List<PerformanceRegressionMetricDelta>
        {
            BuildDelta(
                "MeanWallClockMs",
                Mean(baseline.Measurements, measurement => measurement.WallClockMs),
                Mean(current.Measurements, measurement => measurement.WallClockMs)),
            BuildDelta(
                "ErrorRatePercent",
                ErrorRatePercent(baseline.Measurements),
                ErrorRatePercent(current.Measurements)),
        };

        AddIfBothPresent(
            deltas, "MeanBytesPerSecond",
            MeanNullable(baseline.Measurements, measurement => measurement.BytesPerSecond),
            MeanNullable(current.Measurements, measurement => measurement.BytesPerSecond));

        AddIfBothPresent(
            deltas, "MeanItemsPerSecond",
            MeanNullable(baseline.Measurements, measurement => measurement.ItemsPerSecond),
            MeanNullable(current.Measurements, measurement => measurement.ItemsPerSecond));

        return new PerformanceRegressionReport(current.ScenarioName, baseline.Id, current.Id, deltas, InformativeOnlyNotice);
    }

    private static void AddIfBothPresent(
        List<PerformanceRegressionMetricDelta> deltas, string metricName, double? baselineValue, double? currentValue)
    {
        // Uma métrica ausente em qualquer um dos dois lados (ex.: cenário sem throughput semanticamente
        // aplicável) é OMITIDA do relatório — nunca preenchida com zero, que pareceria uma regressão de
        // 100% inventada.
        if (baselineValue is { } baselineNumber && currentValue is { } currentNumber)
        {
            deltas.Add(BuildDelta(metricName, baselineNumber, currentNumber));
        }
    }

    private static PerformanceRegressionMetricDelta BuildDelta(string metricName, double baselineValue, double currentValue)
    {
        var absoluteDelta = currentValue - baselineValue;
        var percentDelta = baselineValue == 0
            ? (currentValue == 0 ? 0d : double.PositiveInfinity)
            : (absoluteDelta / baselineValue) * 100d;
        return new PerformanceRegressionMetricDelta(metricName, baselineValue, currentValue, absoluteDelta, percentDelta);
    }

    private static double Mean(IReadOnlyList<BenchmarkMeasurement> measurements, Func<BenchmarkMeasurement, double> selector) =>
        measurements.Count == 0 ? 0d : measurements.Average(selector);

    private static double? MeanNullable(IReadOnlyList<BenchmarkMeasurement> measurements, Func<BenchmarkMeasurement, double?> selector)
    {
        var values = measurements.Select(selector).Where(value => value is not null).Select(value => value!.Value).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    private static double ErrorRatePercent(IReadOnlyList<BenchmarkMeasurement> measurements) =>
        measurements.Count == 0
            ? 0d
            : 100d * measurements.Count(measurement => measurement.Outcome != BenchmarkIterationOutcome.Success) / measurements.Count;
}
