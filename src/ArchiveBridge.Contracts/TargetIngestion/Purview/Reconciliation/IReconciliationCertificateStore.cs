using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Porta de persistência do reconciliation certificate (AB-I6-013). Append-only: uma versão nova NUNCA
/// sobrescreve/edita uma anterior. Todo o resultado/gates de negócio (<see cref="ReconciliationOutcome"/>,
/// completude, resumo de desvios) já foram computados pela Application/Domain (<see cref="ReconciliationCertificateRules"/>)
/// ANTES de qualquer chamada a <see cref="IssueOrConvergeAsync"/> — a store nunca reinterpreta essas regras;
/// resolve exclusivamente concorrência/convergência sob lock, detecta evidência alterada durante a emissão
/// (fail-closed, nunca um snapshot misto) e persiste.
/// </summary>
public interface IReconciliationCertificateStore
{
    /// <summary>
    /// Aloca a próxima <see cref="ReconciliationCertificate.CertificateVersion"/> deste escopo (onda/plano)
    /// sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="ReconciliationCertificate.EvaluationFingerprint"/> (item 16, replay idêntico; item 11, N
    /// emissões concorrentes idênticas convergem para uma única versão canônica).
    /// <para>
    /// Revalida, SOB O MESMO LOCK e na MESMA transação: (1) que <paramref name="assessmentVersion"/> ainda é
    /// a versão vigente da avaliação (serializa com <c>SqlReconciliationAssessmentStore.PersistAsync</c> via
    /// a mesma técnica de <c>UPDLOCK/HOLDLOCK</c> já usada pelo Passo 4); e (2) que
    /// <paramref name="expectedDecisionsStateFingerprint"/> (<see cref="ReconciliationExceptionDecisionsStateHash"/>
    /// já computado pela Application a partir das decisões vigentes lidas) ainda corresponde às decisões
    /// vigentes REALMENTE lidas sob o lock. Quando qualquer uma diverge (evidência avançou entre a resolução
    /// do candidato pela Application e este lock), a chamada é recusada com
    /// <see cref="ReconciliationCertificateStaleChainException"/> em vez de persistir um certificate baseado
    /// em snapshot misto (item 17/49).
    /// </para>
    /// </summary>
    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="ReconciliationCertificateStaleChainException">A avaliação ou as dispositions vigentes mudaram sob o lock desde a resolução do candidato.</exception>
    Task<ReconciliationCertificate> IssueOrConvergeAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint,
        Sha256Hash expectedDecisionsStateFingerprint,
        ReconciliationOutcome result,
        int totalItemCount,
        int incompleteItemCount,
        int deviationCount,
        Sha256Hash deviationsSha256,
        bool duplicateRiskDetected,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// O certificate VIGENTE (maior <see cref="ReconciliationCertificate.CertificateVersion"/>) deste escopo
    /// (onda/plano) — <see langword="null"/> se nenhum ainda emitido. Revalida <see cref="ReconciliationCertificate.CertificateHash"/>
    /// contra os campos REALMENTE carregados (fail-closed) — NÃO avalia supersession contra a avaliação
    /// canônica atual (responsabilidade da Application).
    /// </summary>
    /// <exception cref="ReconciliationCertificateIntegrityViolationException">O certificate_hash persistido diverge do recomputado.</exception>
    Task<ReconciliationCertificate?> GetLatestAsync(TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken);

    /// <summary>Uma versão específica do certificate — <see langword="null"/> se inexistente/fora do escopo (anti-IDOR).</summary>
    /// <exception cref="ReconciliationCertificateIntegrityViolationException">O certificate_hash persistido diverge do recomputado.</exception>
    Task<ReconciliationCertificate?> GetByVersionAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int certificateVersion, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) deste escopo (onda/plano), em ordem crescente de versão.</summary>
    Task<IReadOnlyList<ReconciliationCertificate>> GetHistoryAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken);

    /// <summary>
    /// O certificate VIGENTE mais recente da MESMA onda emitido sob um <see cref="PurviewImportJobName"/>
    /// DIFERENTE de <paramref name="excludingPlannedJobName"/> — usado exclusivamente para detectar
    /// <see cref="ReconciliationOutcome.DuplicateRisk"/> entre tentativas distintas da mesma onda (item 27:
    /// "target/root/hash diverge de execução anterior"). <see langword="null"/> quando nenhuma outra
    /// tentativa desta onda já foi certificada.
    /// </summary>
    Task<ReconciliationCertificate?> GetLatestForWaveAcrossOtherAttemptsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName excludingPlannedJobName, CancellationToken cancellationToken);

    /// <summary>
    /// Registra um evento auditável append-only sobre um certificate (item 20: emissão/replay/verificação/
    /// supersession/falha de integridade) — nunca inclui segredo ou PII indevida; apenas metadados técnicos
    /// necessários à responsabilização.
    /// </summary>
    Task RecordAuditEventAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int? certificateVersion,
        ReconciliationCertificateAuditEventType eventType,
        string actorId,
        string actorRole,
        bool succeeded,
        string reason,
        CorrelationId correlation,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}
