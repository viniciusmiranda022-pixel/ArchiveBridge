using ArchiveBridge.Contracts.Approvals;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
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

    /// <summary>
    /// Persiste a transição de estado (status; e responsável/data ao aprovar) com concorrência otimista
    /// por <c>row_version</c>. Quando <paramref name="fence"/> é informado (execução durável), a
    /// gravação valida o cercamento do Job na MESMA transação (state=Processing, owner_worker, época,
    /// lease válido) — um dono defasado não persiste.
    /// </summary>
    Task SaveStatusAsync(
        MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null);

    /// <summary>
    /// Persiste ATOMICAMENTE a validação de capacidade: numa única transação valida o cercamento do
    /// Job (quando <paramref name="fence"/> informado), grava a transição de estado da onda com
    /// concorrência otimista (<c>row_version</c>) e insere as avaliações (append-only). Assim uma falha
    /// não deixa avaliações sem transição nem transição sem avaliações (sem estado parcial). Um retry
    /// que reencontre a onda já validada não duplica (o caso de uso não re-registra).
    /// </summary>
    Task SaveValidationAsync(
        MigrationWave wave,
        IReadOnlyList<PlanningAssessment> assessments,
        CorrelationId correlation,
        CancellationToken cancellationToken,
        JobFence? fence = null);

    /// <summary>
    /// Persiste uma nova versão de seleção (nova wave_version + entradas + totais/hashes) e devolve a
    /// onda a Draft. Bloqueado pelo domínio e por gatilho quando a seleção está congelada (aprovada).
    /// </summary>
    Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste atomicamente a transição de estado (com concorrência otimista) E a linha de decisão
    /// na tabela <c>approvals</c>, em uma única transação. Falha com concorrência se o
    /// <c>row_version</c> divergiu.
    /// </summary>
    Task SaveStatusWithApprovalAsync(MigrationWave wave, ApprovalRecord approval, CancellationToken cancellationToken);
}
