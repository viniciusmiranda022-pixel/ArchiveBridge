using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Waves;

/// <summary>Resultado de <see cref="FreezeWaveUseCase"/>.</summary>
public sealed record WaveFreezeResult(WaveStatus Status);

/// <summary>
/// Corpo de execução do comando durável <c>FreezeWave</c>. Congela uma onda aprovada para execução
/// (Approved → Frozen). A seleção e o destino já são imutáveis desde a aprovação (reforçado por
/// gatilho); o congelamento sela a onda. É idempotente em retry: se já está Frozen, não re-transita.
/// </summary>
public sealed class FreezeWaveUseCase(IWaveStore waves)
{
    private readonly IWaveStore _waves = waves;

    /// <summary>Congela a onda do escopo. Quando <paramref name="fence"/> é informado, o efeito é cercado pelo Job.</summary>
    public async Task<WaveFreezeResult> ExecuteAsync(
        TenantScope scope, WaveId waveId, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null)
    {
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        if (wave.Status == WaveStatus.Frozen)
        {
            return new WaveFreezeResult(wave.Status);
        }

        wave.Freeze();
        await _waves.SaveStatusAsync(wave, correlation, cancellationToken, fence).ConfigureAwait(false);

        return new WaveFreezeResult(wave.Status);
    }
}
