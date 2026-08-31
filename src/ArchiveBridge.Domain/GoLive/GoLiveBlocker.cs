namespace ArchiveBridge.Domain.GoLive;

/// <summary>Um motivo estável que impede <see cref="GoLiveOutcome.GoLiveAuthorized"/> (AB-I8-010) — parte do relatório sanitizado (escopo obrigatório item 12).</summary>
public sealed record GoLiveBlocker(string Code, string ReasonCode)
{
    /// <summary>Nenhum plano de canário existe para este escopo, ou o canário vigente não é <c>CanaryPassed</c>.</summary>
    public const string CanaryNotPassedCode = "CANARY_NOT_PASSED";

    /// <summary>
    /// O Production Readiness Review canônico vigente já não corresponde exatamente (versão + fingerprint) ao
    /// vinculado pelo plano de canário — drift de build/commit/digest/policy/capability/evidência desde o
    /// canário (escopo obrigatório item 3).
    /// </summary>
    public const string ReadinessReviewDriftCode = "READINESS_REVIEW_DRIFT";

    /// <summary>Um controle operacional/M365 (§47.4/§47.5) revalidado FRESCO no instante desta decisão não está <c>Pass</c> (escopo obrigatório item 4).</summary>
    public const string OperationalControlNotPassCode = "OPERATIONAL_CONTROL_NOT_PASS";
}
