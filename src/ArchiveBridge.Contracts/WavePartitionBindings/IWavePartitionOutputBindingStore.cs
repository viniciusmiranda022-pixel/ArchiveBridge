using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;

namespace ArchiveBridge.Contracts.WavePartitionBindings;

/// <summary>
/// Persistência append-only dos vínculos wave → output de particionamento (AB-I5-010). Tenant/projeto
/// scoped (RLS + filtro explícito por <c>project_id</c>). <see cref="SaveAsync"/> lança
/// <see cref="WavePartitionOutputBindingConflictException"/> quando o índice único de canonicidade
/// (tenant, projeto, wave, plano, parte) já foi ocupado por uma corrida concorrente; o chamador deve reler
/// via <see cref="FindCanonicalAsync"/> (nunca tratar como erro de negócio).
/// </summary>
public interface IWavePartitionOutputBindingStore
{
    /// <summary>Vínculo canônico atual para a onda+plano+parte, se houver.</summary>
    Task<WavePartitionOutputBinding?> FindCanonicalAsync(
        TenantScope scope, WaveId wave, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken);

    /// <summary>
    /// Todos os vínculos canônicos da onda (a fonte de autoridade de custódia física consumida pelo upload
    /// Purview, AB-I5-009 item 2) — ordem estável por <c>created_at_utc</c> ascendente.
    /// </summary>
    Task<IReadOnlyList<WavePartitionOutputBinding>> ListForWaveAsync(
        TenantScope scope, WaveId wave, CancellationToken cancellationToken);

    /// <summary>Persiste um novo vínculo (append-only).</summary>
    /// <exception cref="WavePartitionOutputBindingConflictException">Corrida concorrente já gravou o canônico equivalente.</exception>
    Task<WavePartitionOutputBinding> SaveAsync(WavePartitionOutputBinding binding, CancellationToken cancellationToken);
}
