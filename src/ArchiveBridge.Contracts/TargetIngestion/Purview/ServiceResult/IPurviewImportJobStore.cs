using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Porta de persistência dos planos de import job do Purview e das observações do provider (AB-I6-001
/// itens 4-5, 9-10). Planos são append-only, gravados sob lock (mesmo padrão de
/// <c>SqlPurviewMappingCsvStore.ReserveAsync</c>): a sequência de tentativa é alocada N+1 dentro da MESMA
/// transação que insere o plano, eliminando corrida entre leitura e escrita. Observações são append-only e
/// a store aplica, transacionalmente, tanto a convergência idempotente de replay quanto a recusa fail-closed
/// de reassociação de <see cref="PurviewProviderOperationId"/> (item 5) — nunca a Application lendo e
/// decidindo em duas etapas separadas (races).
/// </summary>
public interface IPurviewImportJobStore
{
    /// <summary>
    /// O plano mais recente desta onda cuja <see cref="PurviewImportJobPlan.EvidenceFingerprint"/> coincide
    /// com <paramref name="fingerprint"/> (<see langword="null"/> se nenhum) — recupera, sem nova tentativa,
    /// um plano já criado para a MESMA evidência canônica (replay idempotente, item 10).
    /// </summary>
    Task<PurviewImportJobPlan?> GetLatestPlanByFingerprintAsync(
        TenantScope scope, WaveId wave, PurviewMappingGenerationFingerprint fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Aloca a próxima <see cref="PurviewImportJobPlan.AttemptSequence"/> desta onda sob lock e insere um
    /// novo plano (transação curta) — o nome planejado é derivado DENTRO da mesma transação a partir da
    /// sequência alocada (mesmo padrão de <c>mapping_version</c> em <c>SqlPurviewMappingCsvStore.ReserveAsync</c>).
    /// </summary>
    Task<PurviewImportJobPlan> CreatePlanAsync(
        TenantScope scope,
        WaveId wave,
        PurviewMappingGenerationFingerprint fingerprint,
        string createdBy,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Um plano específico desta onda por nome planejado OPACO — nunca por caminho físico/índice.
    /// <see langword="null"/> se o plano não existir/for de outro escopo (anti-IDOR).
    /// </summary>
    Task<PurviewImportJobPlan?> GetPlanByNameAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken);

    /// <summary>
    /// TODOS os planos (todas as tentativas) desta onda, em qualquer ordem — usado para determinar,
    /// server-side e sem depender de um identificador opaco fornecido pelo caller, se ALGUMA tentativa de
    /// planejamento desta onda já tem evidência observada de progressão (AB-I6-006: gate temporal do
    /// baseline BeforeImport de estatísticas EXO). Lista vazia se a onda ainda não tem nenhum plano.
    /// </summary>
    Task<IReadOnlyList<PurviewImportJobPlan>> GetPlansForWaveAsync(
        TenantScope scope, WaveId wave, CancellationToken cancellationToken);

    /// <summary>A observação mais recente registrada para este plano (<see langword="null"/> se nenhuma).</summary>
    Task<PurviewImportJobObservation?> GetLatestObservationAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken);

    /// <summary>
    /// Registra uma nova observação (transação curta): verifica, sob lock, que nenhum
    /// <see cref="PurviewProviderOperationId"/> incompatível já está associado a este plano nem a outro
    /// plano/onda do escopo (item 5) e que a observação não é um replay idêntico de uma já registrada
    /// (item 10 — devolve a existente sem inserir linha nova). Fail-closed em qualquer reassociação.
    /// </summary>
    /// <exception cref="PurviewImportJobIdentityConflictException">Reassociação de provider ID detectada.</exception>
    Task<PurviewImportJobObservation> RecordObservationAsync(
        TenantScope scope, PurviewImportJobObservation observation, JobFence? fence, CancellationToken cancellationToken);
}
