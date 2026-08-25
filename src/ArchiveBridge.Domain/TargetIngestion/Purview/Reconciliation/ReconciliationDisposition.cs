namespace ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Estado técnico, explícito e não ambíguo de UM item de reconciliação expected-vs-observed (AB-I6-007
/// item 5) — nunca um resultado de reconciliação FINAL: <see cref="Domain.Reconciliation.ReconciliationOutcome"/>
/// (PASS/PASS_WITH_EXPLAINED_EXCEPTIONS/INCONCLUSIVE/FAIL/DUPLICATE_RISK, runbook §26.3) permanece
/// exclusivo de um Passo futuro do EPIC-07, que ainda depende de disposition humana/final, certificate e
/// conclusão de wave/projeto (STOP-THE-LINE deste Passo). Nenhum valor aqui fecha onda/projeto, autoriza
/// decommission do EV ou emite certificate.
/// </summary>
public enum ReconciliationDisposition
{
    /// <summary>
    /// Todas as evidências obrigatórias para este item estão presentes, íntegras e semanticamente
    /// conclusivas, e nenhuma divergência concreta foi observada (item 5: "Matched só pode existir quando
    /// todas as evidências obrigatórias... estão presentes, íntegras e semanticamente conclusivas").
    /// </summary>
    MatchedWithinEvidence,

    /// <summary>
    /// Divergência observável CONCRETA (ex.: status <c>Failed</c>/<c>SkippedOrCorrupted</c> do service
    /// result, ou uma métrica de archive que diminuiu entre <c>BeforeImport</c> e <c>AfterImport</c>).
    /// Nunca inventado a partir de ausência de dado (item 5: "ausência de dado é Unknown/Incomplete, não
    /// Mismatch inventado").
    /// </summary>
    Mismatch,

    /// <summary>
    /// Evidência insuficiente/inconclusiva para decidir <see cref="MatchedWithinEvidence"/> ou
    /// <see cref="Mismatch"/>: PST esperado ausente do provider result, status/contador
    /// <c>Unknown/NotReported</c>, ou <c>Before</c>/<c>After</c> do archive ainda não capturados/ambos
    /// desconhecidos. <c>Unknown/NotReported</c> NUNCA vira zero, match ou sucesso por default (item 5).
    /// </summary>
    IncompleteEvidence,

    /// <summary>
    /// A evidência resolvida para este item falhou uma checagem de integridade estrutural própria deste
    /// Passo (ex.: snapshot de archive/fase cruzados — item 8: "Nunca comparar snapshots de escopos,
    /// archives ou phases diferentes") — o item é bloqueado explicitamente, nunca comparado como se fosse
    /// válido.
    /// </summary>
    BlockedIntegrity,

    /// <summary>
    /// Um item observado no provider result (service result do Purview) que NÃO pertence ao conjunto
    /// esperado resolvido server-side para esta onda (item 7: "Itens extras no provider result não podem
    /// ser ignorados silenciosamente") — aparece explicitamente como exceção de reconciliação, nunca
    /// descartado.
    /// </summary>
    ExtraInProvider,
}
