using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Waves;

/// <summary>
/// Porta de persistência do agregado <see cref="MigrationWave"/>. Persiste a onda, o histórico
/// imutável de versões de seleção e as entradas (metadados de PST — nunca conteúdo). Congelamento
/// pós-aprovação é reforçado por gatilho no banco. Escopo de tenant/projeto obrigatório em toda
/// operação (RLS + filtro por project_id).
/// </summary>
public interface IWaveStore
{
    /// <summary>Cria a onda em Draft com a versão de seleção inicial e suas entradas.</summary>
    Task AddAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>Lê a onda e a seleção corrente; <see langword="null"/> se inexistente/de outro tenant.</summary>
    Task<MigrationWave?> GetAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>Persiste a transição de estado (status; e responsável/data ao aprovar).</summary>
    Task SaveStatusAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste uma nova versão de seleção (nova wave_version + entradas + totais/hashes) e devolve a
    /// onda a Draft. Bloqueado pelo domínio e por gatilho quando a seleção está congelada (aprovada).
    /// </summary>
    Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken);
}
