using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Planning;

/// <summary>
/// Porta de registro (append-only) das avaliações de capacidade. Cada avaliação é evidência imutável:
/// volume por archive, regra aplicada, resultado, motivo, correlação, data e (quando houver) o
/// responsável pela liberação. Nunca contém conteúdo, segredo ou token.
/// </summary>
public interface IPlanningStore
{
    /// <summary>Registra as avaliações de capacidade de uma versão de seleção da onda.</summary>
    Task RecordAsync(
        TenantScope scope,
        WaveId waveId,
        WaveVersion waveVersion,
        IReadOnlyList<PlanningAssessment> assessments,
        CancellationToken cancellationToken);
}
