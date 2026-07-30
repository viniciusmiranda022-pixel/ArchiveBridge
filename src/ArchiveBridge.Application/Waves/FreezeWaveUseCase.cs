using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Waves;

/// <summary>Resultado de <see cref="FreezeWaveUseCase"/>.</summary>
public sealed record WaveFreezeResult(JobId Job, WaveStatus Status);

/// <summary>
/// Comando FreezeWave: congela uma onda aprovada para execução (Approved → Frozen). A seleção e o
/// destino já são imutáveis desde a aprovação (reforçado por gatilho); o congelamento sela a onda
/// para a fase de execução. Emite um Job de controle durável correlacionado. Transições inválidas
/// falham de forma fechada.
/// </summary>
public sealed class FreezeWaveUseCase(IWaveStore waves, IJobStore jobs)
{
    private readonly IWaveStore _waves = waves;
    private readonly IJobStore _jobs = jobs;

    /// <summary>Congela a onda do escopo.</summary>
    public async Task<WaveFreezeResult> ExecuteAsync(
        TenantScope scope, WaveId waveId, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        wave.Freeze();

        var jobId = await _jobs.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, JobPriority.Normal, correlation), cancellationToken)
            .ConfigureAwait(false);
        await _waves.SaveStatusAsync(wave, correlation, cancellationToken).ConfigureAwait(false);

        return new WaveFreezeResult(jobId, wave.Status);
    }
}
