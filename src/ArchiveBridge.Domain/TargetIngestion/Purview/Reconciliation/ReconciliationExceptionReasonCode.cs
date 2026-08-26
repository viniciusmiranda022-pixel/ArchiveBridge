namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Catálogo FECHADO e VERSIONADO de motivos controlados para uma decisão de disposition (item 15 do work
/// order AB-I6-010): texto livre (<c>Comment</c>) nunca é a única autoridade semântica de uma decisão — todo
/// registro carrega, além do comentário opcional, um código deste catálogo e a versão do catálogo vigente no
/// instante da decisão (<see cref="ReconciliationExceptionReasonCodeCatalog.CurrentVersion"/>), preservando a
/// interpretação de decisões históricas mesmo que o catálogo evolua em versões futuras.
/// </summary>
public enum ReconciliationExceptionReasonCode : byte
{
    /// <summary>
    /// Divergência tolerada por política operacional (ex.: latência de relato do provider já conhecida e
    /// sem impacto material) — usável para <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/>
    /// sobre um item <see cref="ReconciliationDisposition.Mismatch"/> ou <see cref="ReconciliationDisposition.ExtraInProvider"/>.
    /// </summary>
    ToleratedByOperationalPolicy = 0,

    /// <summary>
    /// Peculiaridade conhecida e não bloqueante do provider (Purview), já catalogada como benigna — usável
    /// para <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> sobre
    /// <see cref="ReconciliationDisposition.Mismatch"/> ou <see cref="ReconciliationDisposition.ExtraInProvider"/>.
    /// </summary>
    KnownNonBlockingProviderQuirk = 1,

    /// <summary>
    /// Remediação agendada por reimportação do PST/archive afetado — usável para
    /// <see cref="ReconciliationExceptionDecisionStatus.RemediationRequired"/> sobre
    /// <see cref="ReconciliationDisposition.Mismatch"/> ou <see cref="ReconciliationDisposition.ExtraInProvider"/>.
    /// </summary>
    RemediationScheduledReimportRequired = 2,

    /// <summary>
    /// Remediação agendada por verificação/captura manual pendente — usável para
    /// <see cref="ReconciliationExceptionDecisionStatus.RemediationRequired"/> sobre
    /// <see cref="ReconciliationDisposition.IncompleteEvidence"/>.
    /// </summary>
    RemediationScheduledManualVerificationRequired = 3,

    /// <summary>
    /// Único motivo aceito para aceitar um item <see cref="ReconciliationDisposition.IncompleteEvidence"/>
    /// como <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> (item 12 do work order:
    /// "requer decisão humana explícita e motivo auditável") — a evidência incompleta NUNCA vira sucesso
    /// técnico; este código é exclusivamente a marca explícita e auditável da decisão operacional de aceitar
    /// o risco residual, nunca aplicável a nenhum outro <see cref="ReconciliationDisposition"/>.
    /// </summary>
    IncompleteEvidenceAcceptedByExplicitOperationalPolicy = 4,

    /// <summary>
    /// Item extra observado no provider confirmado como benigno (ex.: reprocessamento intencional já
    /// entendido) — usável para <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> sobre
    /// <see cref="ReconciliationDisposition.ExtraInProvider"/>.
    /// </summary>
    ExtraProviderItemConfirmedBenign = 5,

    /// <summary>
    /// Único motivo aceito para <see cref="ReconciliationExceptionDecisionStatus.Rejected"/> — a decisão
    /// solicitada não tinha justificativa suficiente; a exceção permanece pendente.
    /// </summary>
    DecisionRejectedInsufficientJustification = 6,
}

/// <summary>Metadados de versionamento do catálogo fechado de <see cref="ReconciliationExceptionReasonCode"/>.</summary>
public static class ReconciliationExceptionReasonCodeCatalog
{
    /// <summary>Versão vigente do catálogo — gravada em toda decisão nova (item 15/19: aditivo, nunca reescreve histórico).</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Indica se o código é um membro reconhecido do enum (defesa contra valores fora de faixa desserializados/persistidos).</summary>
    public static bool IsKnownCode(ReconciliationExceptionReasonCode code) =>
        code is >= ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy
            and <= ReconciliationExceptionReasonCode.DecisionRejectedInsufficientJustification;
}
