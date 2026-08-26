namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Estado explícito de UMA decisão humana/auditável sobre UMA exceção técnica de reconciliação já
/// materializada pelo Passo 3 (AB-I6-010). Nunca equivale a certificate, <c>ReconciliationOutcome</c> ou
/// conclusão de wave/projeto (STOP-THE-LINE): adiciona uma camada de decisão auditável POR CIMA do
/// resultado técnico (<see cref="ReconciliationDisposition"/>), sem alterá-lo nem mascará-lo.
/// </summary>
public enum ReconciliationExceptionDecisionStatus
{
    /// <summary>
    /// Nenhuma decisão foi registrada ainda para esta exceção NESTA versão de avaliação — estado implícito
    /// (nunca persistido como linha própria; devolvido pelo read model quando <c>GetCurrentAsync</c> não
    /// encontra nenhuma decisão). Nunca pode ser o status EXPLICITAMENTE solicitado por um caller.
    /// </summary>
    Pending,

    /// <summary>
    /// A exceção foi aceita como desvio operacional explícito, com motivo controlado — NUNCA torna o item
    /// tecnicamente <c>MatchedWithinEvidence</c> nem participa de um certificate/PASS terminal.
    /// </summary>
    AcceptedException,

    /// <summary>A exceção requer remediação (ex.: reimportação, correção manual) antes de qualquer aceitação.</summary>
    RemediationRequired,

    /// <summary>
    /// A decisão solicitada foi recusada como não fundamentada (motivo/catálogo insuficiente) — a exceção
    /// permanece pendente de uma nova decisão válida.
    /// </summary>
    Rejected,
}
