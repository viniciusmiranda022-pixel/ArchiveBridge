using System.Diagnostics;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Application.Performance;

/// <summary>Desfecho de UMA iteração devolvido pelo workload de um cenário de benchmark ao harness.</summary>
/// <param name="Outcome">Desfecho observado (uma exceção não tratada do workload é convertida em <see cref="BenchmarkIterationOutcome.Error"/> automaticamente).</param>
/// <param name="BytesProcessed">Bytes processados nesta iteração, quando aplicável ao cenário.</param>
/// <param name="ItemsProcessed">Itens processados nesta iteração, quando aplicável ao cenário.</param>
public sealed record BenchmarkWorkloadOutcome(BenchmarkIterationOutcome Outcome, long? BytesProcessed, long? ItemsProcessed)
{
    /// <summary>Constrói um desfecho de sucesso com as contagens processadas informadas.</summary>
    public static BenchmarkWorkloadOutcome Success(long? bytesProcessed = null, long? itemsProcessed = null) =>
        new(BenchmarkIterationOutcome.Success, bytesProcessed, itemsProcessed);
}

/// <summary>
/// Harness de benchmark reproduzível (AB-I7-003 §1): executa um workload por N iterações (após um
/// aquecimento descartado), medindo wall-clock, tempo de CPU do processo e peak working set — sempre os
/// MESMOS quatro metadados de reprodutibilidade (versão de build, runtime, host profile, dataset) e sempre
/// produzindo exatamente uma <see cref="BenchmarkMeasurement"/> por iteração medida, mesmo quando o
/// workload falha (erro é evidência, nunca omitido silenciosamente). Não impõe threshold algum — apenas
/// mede e registra; decisão sobre o que fazer com a medição é de quem chama.
/// </summary>
public sealed class BenchmarkHarness(IClock clock)
{
    private readonly IClock _clock = clock;

    /// <summary>
    /// Executa o cenário e devolve o registro completo pronto para persistência/comparação. Cancelamento do
    /// <paramref name="cancellationToken"/> pelo CHAMADOR sempre propaga (nunca é registrado como uma
    /// medição); uma exceção lançada pelo próprio <paramref name="workload"/> nunca propaga — vira uma
    /// medição com <see cref="BenchmarkIterationOutcome.Error"/>, e as iterações seguintes continuam.
    /// </summary>
    public async Task<PerformanceBenchmarkRunRecord> RunAsync(
        TenantScope scope,
        string scenarioName,
        string buildVersion,
        string runtimeDescription,
        string hostProfile,
        BenchmarkDatasetDescriptor dataset,
        int warmupIterations,
        int iterations,
        Func<int, CancellationToken, Task<BenchmarkWorkloadOutcome>> workload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(dataset);

        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "iterations precisa ser pelo menos 1.");
        }

        if (warmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupIterations), "warmupIterations não pode ser negativo.");
        }

        for (var warmup = 0; warmup < warmupIterations; warmup++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunWarmupIterationAsync(warmup, workload, cancellationToken).ConfigureAwait(false);
        }

        var process = Process.GetCurrentProcess();
        var measurements = new List<BenchmarkMeasurement>(iterations);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();

            BenchmarkIterationOutcome outcome;
            long? bytesProcessed = null;
            long? itemsProcessed = null;

            try
            {
                var result = await workload(iteration, cancellationToken).ConfigureAwait(false);
                outcome = result.Outcome;
                bytesProcessed = result.BytesProcessed;
                itemsProcessed = result.ItemsProcessed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // cancelamento do CHAMADOR sempre propaga — nunca vira uma medição.
            }
            catch (Exception)
            {
                // Sanitizado por construção: nenhuma mensagem/stack trace da exceção entra na evidência —
                // apenas o desfecho Error (AB-I7-003 §9: sem PII/segredo nos resultados).
                outcome = BenchmarkIterationOutcome.Error;
            }

            stopwatch.Stop();
            process.Refresh();
            var cpuAfter = process.TotalProcessorTime;

            measurements.Add(new BenchmarkMeasurement(
                iteration,
                stopwatch.Elapsed.TotalMilliseconds,
                (cpuAfter - cpuBefore).TotalMilliseconds,
                process.PeakWorkingSet64,
                bytesProcessed,
                itemsProcessed,
                outcome));
        }

        return PerformanceBenchmarkRunRecord.Complete(
            PerformanceBenchmarkRunId.New(),
            scope.Tenant,
            scope.Project,
            scenarioName,
            buildVersion,
            runtimeDescription,
            hostProfile,
            dataset,
            warmupIterations,
            iterations,
            measurements,
            _clock.UtcNow);
    }

    private static async Task RunWarmupIterationAsync(
        int index, Func<int, CancellationToken, Task<BenchmarkWorkloadOutcome>> workload, CancellationToken cancellationToken)
    {
        try
        {
            await workload(index, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Erro durante AQUECIMENTO nunca é evidência nem interrompe o harness — apenas as iterações
            // medidas (loop principal) registram Error como desfecho observado.
        }
    }
}
