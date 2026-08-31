namespace ArchiveBridge.Domain.GoLive;

/// <summary>
/// Desfecho agregado de UMA decisão de go-live (AB-I8-010). DELIBERADAMENTE possui apenas estes dois valores —
/// não existe, e nunca deve existir, um caso <c>Completed</c> (STOP-THE-LINE do work order: este Passo nunca
/// marca migração/projeto/wave concluído — ver <see cref="ArchiveBridge.Domain.MigrationCompletion.MigrationCompletionOutcome"/>,
/// um gate inteiramente separado e sem dependência estrutural deste). <see cref="Blocked"/> é o default
/// fail-closed (valor 0).
/// </summary>
public enum GoLiveOutcome : byte
{
    /// <summary>
    /// Ao menos um dos gates obrigatórios (canário canônico ainda vigente/CanaryPassed, ausência de drift do
    /// Production Readiness Review vinculado, ou revalidação fresca dos controles operacionais/M365) não está
    /// satisfeito — fail-closed default.
    /// </summary>
    Blocked = 0,

    /// <summary>
    /// O canário canônico vinculado é <c>CanaryPassed</c> (o que inclui a aprovação humana da primeira onda,
    /// §48 item 185), o Production Readiness Review vigente ainda corresponde EXATAMENTE ao vinculado pelo
    /// canário (nenhum drift de build/commit/digest/policy/capability desde o canário), e a revalidação fresca
    /// dos controles operacionais/M365 (§47.4/§47.5, escopo obrigatório item 4) está, no instante desta
    /// decisão, integralmente <c>Pass</c>. Mesmo neste estado, este tipo NUNCA representa a migração
    /// <c>Completed</c> — apenas que a primeira onda real de baixa criticidade está autorizada a prosseguir
    /// (runbook §48 item 185); os critérios de encerramento do §49 permanecem inteiramente fora do escopo
    /// executável deste desfecho.
    /// </summary>
    GoLiveAuthorized = 1,
}
