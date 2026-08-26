namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Regras semânticas PURAS e determinísticas do workflow de disposition (AB-I6-010, "Regras semânticas
/// mínimas" do work order) — nunca depende de infraestrutura, RBAC concreto ou I/O. A autorização RBAC
/// concreta (quais papéis do portal satisfazem <see cref="RequiresElevatedAuthorization"/>) é resolvida pela
/// Application (que conhece o catálogo de papéis de <c>Contracts.ControlPlane.PortalRoles</c>); esta classe
/// apenas expressa QUANDO uma autorização elevada é exigida, não QUAL papel concreto a satisfaz.
/// </summary>
public static class ReconciliationExceptionDispositionRules
{
    /// <summary>
    /// Exige que o item seja uma exceção técnica genuína disponível para disposition — nunca
    /// <see cref="ReconciliationDisposition.MatchedWithinEvidence"/> (item 11: não é uma exceção) nem
    /// <see cref="ReconciliationDisposition.BlockedIntegrity"/> (item 13: indeclinável/inaudível como
    /// sucesso; somente nova evidência/reconciliação válida remove o bloqueio, nunca uma decisão humana).
    /// </summary>
    /// <exception cref="ReconciliationExceptionNotDispositionableException">O item não é uma exceção passível de disposition.</exception>
    public static void EnsureDispositionable(ReconciliationDisposition technicalDisposition)
    {
        if (technicalDisposition == ReconciliationDisposition.MatchedWithinEvidence)
        {
            throw new ReconciliationExceptionNotDispositionableException(
                "Um item MatchedWithinEvidence não é uma exceção de reconciliação — disposition recusada (fail-closed).");
        }

        if (technicalDisposition == ReconciliationDisposition.BlockedIntegrity)
        {
            throw new ReconciliationExceptionNotDispositionableException(
                "Um item BlockedIntegrity nunca pode ser aceito nem dispensado como exceção operacional — permanece " +
                "bloqueado até reparo/reconciliação válida (fail-closed).");
        }
    }

    /// <summary>
    /// O status EXPLICITAMENTE solicitado por um caller nunca pode ser
    /// <see cref="ReconciliationExceptionDecisionStatus.Pending"/> — este é somente o estado implícito de
    /// "nenhuma decisão ainda", nunca uma decisão em si.
    /// </summary>
    /// <exception cref="ReconciliationExceptionDispositionValidationException"><paramref name="requestedStatus"/> é <see cref="ReconciliationExceptionDecisionStatus.Pending"/>.</exception>
    public static void EnsureStatusIsExplicitlyDecidable(ReconciliationExceptionDecisionStatus requestedStatus)
    {
        if (requestedStatus == ReconciliationExceptionDecisionStatus.Pending)
        {
            throw new ReconciliationExceptionDispositionValidationException(
                "Pending não é um status que pode ser solicitado explicitamente — representa apenas a ausência de decisão (fail-closed).");
        }
    }

    /// <summary>
    /// Verdadeiro quando a combinação (resultado técnico, status solicitado) exige uma autorização RBAC
    /// ELEVADA além do mínimo geral de disposition (item 12 do work order: aceitar
    /// <see cref="ReconciliationDisposition.IncompleteEvidence"/> como
    /// <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> "requer decisão humana explícita" —
    /// tratada aqui como a única transição que exige o papel mais restrito do portal).
    /// </summary>
    public static bool RequiresElevatedAuthorization(ReconciliationDisposition technicalDisposition, ReconciliationExceptionDecisionStatus requestedStatus) =>
        technicalDisposition == ReconciliationDisposition.IncompleteEvidence
        && requestedStatus == ReconciliationExceptionDecisionStatus.AcceptedException;

    /// <summary>
    /// Valida que <paramref name="reasonCode"/> pertence ao catálogo fechado vigente
    /// (<paramref name="reasonCodeCatalogVersion"/>) e é semanticamente compatível com a combinação
    /// (resultado técnico, status solicitado) — nunca permite texto livre como única autoridade semântica
    /// (item 15).
    /// </summary>
    /// <exception cref="ReconciliationExceptionDispositionValidationException">Motivo desconhecido, catálogo divergente, ou combinação motivo/status/resultado técnico não permitida.</exception>
    public static void EnsureReasonCodeAllowed(
        ReconciliationDisposition technicalDisposition,
        ReconciliationExceptionDecisionStatus requestedStatus,
        ReconciliationExceptionReasonCode reasonCode,
        byte reasonCodeCatalogVersion)
    {
        if (reasonCodeCatalogVersion != ReconciliationExceptionReasonCodeCatalog.CurrentVersion)
        {
            throw new ReconciliationExceptionDispositionValidationException(
                $"A versão do catálogo de motivos informada ({reasonCodeCatalogVersion}) não é a vigente " +
                $"({ReconciliationExceptionReasonCodeCatalog.CurrentVersion}) — recusada (fail-closed).");
        }

        if (!ReconciliationExceptionReasonCodeCatalog.IsKnownCode(reasonCode))
        {
            throw new ReconciliationExceptionDispositionValidationException(
                "O código de motivo informado não pertence ao catálogo fechado vigente (fail-closed).");
        }

        var allowed = requestedStatus switch
        {
            ReconciliationExceptionDecisionStatus.RemediationRequired => technicalDisposition == ReconciliationDisposition.IncompleteEvidence
                ? reasonCode == ReconciliationExceptionReasonCode.RemediationScheduledManualVerificationRequired
                : reasonCode == ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired,

            ReconciliationExceptionDecisionStatus.AcceptedException => technicalDisposition switch
            {
                ReconciliationDisposition.IncompleteEvidence =>
                    reasonCode == ReconciliationExceptionReasonCode.IncompleteEvidenceAcceptedByExplicitOperationalPolicy,
                ReconciliationDisposition.ExtraInProvider => reasonCode is
                    ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy or
                    ReconciliationExceptionReasonCode.KnownNonBlockingProviderQuirk or
                    ReconciliationExceptionReasonCode.ExtraProviderItemConfirmedBenign,
                ReconciliationDisposition.Mismatch => reasonCode is
                    ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy or
                    ReconciliationExceptionReasonCode.KnownNonBlockingProviderQuirk,
                _ => false,
            },

            ReconciliationExceptionDecisionStatus.Rejected =>
                reasonCode == ReconciliationExceptionReasonCode.DecisionRejectedInsufficientJustification,

            _ => false,
        };

        if (!allowed)
        {
            throw new ReconciliationExceptionDispositionValidationException(
                $"O motivo '{reasonCode}' não é permitido para status '{requestedStatus}' sobre um item " +
                $"'{technicalDisposition}' — catálogo fechado recusa a combinação (fail-closed).");
        }
    }
}
