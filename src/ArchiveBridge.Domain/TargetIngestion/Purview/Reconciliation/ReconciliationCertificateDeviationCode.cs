namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Código FECHADO e determinístico do motivo pelo qual UM item não-Matched contribuiu para o desfecho do
/// certificate (AB-I6-013 item 10: "códigos de desvio... estruturado") — sempre derivado exclusivamente do
/// <see cref="ReconciliationDisposition"/> técnico do item e da decisão vigente sobre ele (nunca de texto
/// livre, nunca inventado pela Application). Nunca mascarado por comentário/disposition humana (item 8):
/// mesmo quando a decisão é <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/>, o código
/// de desvio original permanece <see cref="ExplainedException"/> — nunca vira "sem desvio" nem some da
/// contagem estrutural.
/// </summary>
public enum ReconciliationCertificateDeviationCode : byte
{
    /// <summary>
    /// Item <see cref="ReconciliationDisposition.IncompleteEvidence"/>: evidência insuficiente/inconclusiva —
    /// bloqueia <c>PASS</c>/<c>PASS_WITH_EXPLAINED_EXCEPTIONS</c> independentemente de disposition (item 4).
    /// </summary>
    IncompleteEvidence = 0,

    /// <summary>
    /// Item <see cref="ReconciliationDisposition.BlockedIntegrity"/>: falha de integridade estrutural própria
    /// da avaliação — indeclinável, nunca dispositionable (item 5), prevalece sobre qualquer disposition.
    /// </summary>
    BlockedIntegrity = 1,

    /// <summary>
    /// Item <see cref="ReconciliationDisposition.Mismatch"/> ou <see cref="ReconciliationDisposition.ExtraInProvider"/>
    /// SEM decisão vigente <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> — divergência
    /// material não explicada (item 6/40), nunca mascarada por comentário.
    /// </summary>
    UnexplainedException = 2,

    /// <summary>
    /// Item <see cref="ReconciliationDisposition.Mismatch"/> ou <see cref="ReconciliationDisposition.ExtraInProvider"/>
    /// com decisão vigente <see cref="ReconciliationExceptionDecisionStatus.AcceptedException"/> — permite
    /// <c>PASS_WITH_EXPLAINED_EXCEPTIONS</c> quando todos os demais gates fecham (item 62: a disposition
    /// nunca altera o fato técnico de origem, apenas permite o resultado terminal com ressalva explícita).
    /// </summary>
    ExplainedException = 3,
}
