using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Porta de persistência das avaliações de reconciliação expected-vs-observed e dos seus itens filhos de
/// PST/archive (AB-I6-007 itens 10-11). Append-only: uma versão nova nunca sobrescreve/edita uma anterior.
/// <paramref name="pstItems"/>/<paramref name="archiveItems"/> já foram computados PURAMENTE pela
/// Application (<see cref="Domain.TargetIngestion.Purview.Reconciliation.ReconciliationPstCorrelation"/>/
/// <see cref="Domain.TargetIngestion.Purview.Reconciliation.ReconciliationArchiveCorrelation"/>) antes de
/// qualquer chamada a <see cref="PersistAsync"/> — a store nunca reinterpreta regras de negócio, apenas
/// resolve a versão sob lock e persiste.
/// </summary>
public interface IReconciliationAssessmentStore
{
    /// <summary>
    /// Aloca a próxima <see cref="ReconciliationAssessment.AssessmentVersion"/> deste escopo (onda/plano)
    /// sob lock — ou converge para uma versão já persistida com a MESMA
    /// <see cref="ReconciliationAssessment.SourceFingerprint"/> (item 10, replay idempotente), computada
    /// internamente a partir de <paramref name="mappingFingerprint"/>/<paramref name="reportVersion"/>/
    /// <paramref name="reportContentSha256"/>/<paramref name="archiveEvidence"/> — e persiste, numa única
    /// transação curta, o header e os itens filhos de PST/archive (nunca em transações separadas — nenhuma
    /// versão "parcial" é jamais visível).
    /// </summary>
    Task<ReconciliationAssessment> PersistAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        Sha256Hash mappingFingerprint,
        int? reportVersion,
        Sha256Hash? reportContentSha256,
        IReadOnlyList<ReconciliationArchiveEvidenceRef> archiveEvidence,
        IReadOnlyList<PstReconciliationItem> pstItems,
        IReadOnlyList<ArchiveReconciliationItem> archiveItems,
        CorrelationId correlation,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// A avaliação mais recente deste escopo (onda/plano) — <see langword="null"/> se nenhuma ainda
    /// computada. NÃO revalida drift contra a evidência-fonte atual (responsabilidade da Application, item
    /// 4/12) — apenas a integridade do header/itens filhos REALMENTE persistidos.
    /// </summary>
    Task<ReconciliationAssessment?> GetLatestAsync(TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken);

    /// <summary>
    /// Os itens de PST de uma versão específica, revalidados (fail-closed) contra a evidência persistida
    /// (contagem + hash agregado) na reidratação — tampering de qualquer item nunca é devolvido como
    /// válido.
    /// </summary>
    /// <exception cref="Domain.TargetIngestion.Purview.Reconciliation.ReconciliationIntegrityViolationException">Item(ns) adulterado(s) ou hash agregado divergente.</exception>
    Task<IReadOnlyList<PstReconciliationItem>> GetPstItemsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Os itens de archive de uma versão específica, revalidados (fail-closed) contra a evidência
    /// persistida na reidratação.
    /// </summary>
    /// <exception cref="Domain.TargetIngestion.Purview.Reconciliation.ReconciliationIntegrityViolationException">Item(ns) adulterado(s) ou hash agregado divergente.</exception>
    Task<IReadOnlyList<ArchiveReconciliationItem>> GetArchiveItemsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion, CancellationToken cancellationToken);
}
