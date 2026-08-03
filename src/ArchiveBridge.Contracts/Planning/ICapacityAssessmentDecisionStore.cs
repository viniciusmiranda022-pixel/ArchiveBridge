using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Planning;

/// <summary>
/// Porta de registro (append-only) das decisões de avaliação Microsoft e da liberação controlada de
/// ondas bloqueadas por capacidade. A decisão é evidência imutável (nunca altera/apaga a avaliação
/// anterior). Somente uma decisão aprovada, vinculada à versão CORRENTE da onda e ao archive avaliado,
/// libera <c>Blocked → Validating</c> — e essa liberação é gravada ATOMICAMENTE junto da decisão.
/// </summary>
public interface ICapacityAssessmentDecisionStore
{
    /// <summary>
    /// Registra a decisão (append-only). Quando <paramref name="unblock"/> é verdadeiro (decisão
    /// aprovada), grava na MESMA transação a transição da onda para <c>Validating</c> com concorrência
    /// otimista (<c>row_version</c>) e, se informado, o cercamento do Job. A onda passada já reflete o
    /// estado alvo (o domínio transita antes de gravar).
    /// </summary>
    Task RecordAsync(
        MigrationWave wave,
        CapacityAssessmentDecision decision,
        bool unblock,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Archives da versão informada da onda com decisão APROVADA vigente, mapeados ao responsável pela
    /// decisão. Usado pela validação para tratar como liberados os archives acima de 100 GB que já têm
    /// decisão aprovada da versão corrente.
    /// </summary>
    Task<IReadOnlyDictionary<TargetArchiveId, string>> GetApprovedAsync(
        TenantScope scope, WaveId waveId, WaveVersion waveVersion, CancellationToken cancellationToken);
}
