using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Porta de persistência do workflow de disposition humano/auditável (AB-I6-010) sobre exceções técnicas de
/// reconciliação já materializadas pelo Passo 3. Append-only: uma versão de decisão nova NUNCA sobrescreve/
/// edita uma anterior. A store nunca reinterpreta regras de negócio (RBAC, quais transições são permitidas,
/// catálogo de motivos) — essas já foram aplicadas pela Application/Domain antes de qualquer chamada; a
/// store resolve exclusivamente concorrência/convergência sob lock e persiste.
/// </summary>
public interface IReconciliationExceptionDispositionStore
{
    /// <summary>
    /// Aloca a próxima <see cref="ReconciliationExceptionDecision.DecisionVersion"/> desta exceção
    /// (onda, plano, versão de avaliação, item) sob lock — ou converge idempotentemente para uma versão já
    /// persistida com o MESMO <see cref="ReconciliationExceptionDecision.DecisionFingerprint"/> (item 9,
    /// replay idêntico). Quando o candidato NÃO converge e <paramref name="expectedCurrentDecisionVersion"/>
    /// não corresponde à versão vigente sob o MESMO lock, a chamada é recusada com
    /// <see cref="ConcurrencyException"/> (item 10 — decisões conflitantes concorrentes são detectadas e
    /// serializadas, nunca resolvidas por last-write-wins silencioso). <paramref name="expectedCurrentDecisionVersion"/>
    /// igual a 0 significa "nenhuma decisão ainda esperada" para esta exceção nesta versão de avaliação.
    /// </summary>
    /// <exception cref="Domain.TargetIngestion.Purview.ServiceResult.PurviewImportJobSourceNotFoundException">Onda/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="ReconciliationExceptionStaleAssessmentException"><paramref name="assessmentVersion"/> não é mais a versão vigente da avaliação (superseded).</exception>
    /// <exception cref="ConcurrencyException">Uma decisão conflitante já é a vigente sob o lock (versão esperada divergente, sem convergência de fingerprint).</exception>
    Task<ReconciliationExceptionDecision> SaveDecisionAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        Sha256Hash assessmentSourceFingerprint,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        ReconciliationDisposition technicalDisposition,
        int expectedCurrentDecisionVersion,
        ReconciliationExceptionDecisionStatus status,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion,
        string? comment,
        string decidedBy,
        string decidedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// A decisão VIGENTE (maior <c>DecisionVersion</c>) desta exceção nesta versão de avaliação —
    /// <see langword="null"/> se nenhuma decisão ainda foi registrada (estado implícito
    /// <see cref="ReconciliationExceptionDecisionStatus.Pending"/>). Revalida <see cref="ReconciliationExceptionDecision.DecisionFingerprint"/>/
    /// <see cref="ReconciliationExceptionDecision.DecisionHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    Task<ReconciliationExceptionDecision?> GetCurrentAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// TODAS as decisões (histórico completo, append-only) desta exceção nesta versão de avaliação, em
    /// ordem crescente de <c>DecisionVersion</c> — nunca omite/edita uma versão anterior.
    /// </summary>
    Task<IReadOnlyList<ReconciliationExceptionDecision>> GetHistoryAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        ReconciliationExceptionItemKind itemKind,
        string itemKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// A decisão VIGENTE de CADA exceção (item, `DecisionVersion` mais alto) desta versão de avaliação —
    /// uma linha por item que já recebeu ao menos uma decisão; itens ainda sem nenhuma decisão simplesmente
    /// não aparecem (o read model de backlog, <see cref="ReconciliationExceptionWaveBacklog.From"/>, trata a
    /// ausência como <see cref="ReconciliationExceptionDecisionStatus.Pending"/>). Usada para compor o
    /// backlog de uma wave sem uma consulta por item (item 14).
    /// </summary>
    Task<IReadOnlyList<ReconciliationExceptionDecision>> GetCurrentDecisionsForAssessmentAsync(
        TenantScope scope,
        WaveId wave,
        PurviewImportJobName plannedJobName,
        int assessmentVersion,
        CancellationToken cancellationToken);
}
