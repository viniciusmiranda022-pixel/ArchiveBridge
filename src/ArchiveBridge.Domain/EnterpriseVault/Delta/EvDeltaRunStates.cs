namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Desfecho TERMINAL de uma execução de fase de delta (AB-4C-008 req 1/13/14). Cada execução produz
/// exatamente um desfecho, persistido de forma append-only — a história de execuções nunca é reescrita,
/// apenas estendida. <see cref="StrategyUnsupported"/> e <see cref="WatermarkRejected"/> NUNCA chegam a
/// invocar o adapter EV (bloqueados antes de qualquer efeito externo, mesmo padrão de
/// <see cref="Export.EvExportAttemptOutcome.CapabilityBlocked"/>).
/// </summary>
public enum EvDeltaRunOutcome
{
    /// <summary>Concluída com sucesso: watermark emitido, validado e persistido como novo canônico.</summary>
    Completed,

    /// <summary>O adapter EV falhou ao emitir o watermark (transitório) — retryable.</summary>
    Failed,

    /// <summary>Bloqueada antes de qualquer chamada ao adapter: nenhuma delta strategy elegível (fail-closed).</summary>
    StrategyUnsupported,

    /// <summary>Bloqueada antes de qualquer chamada ao adapter: watermark anterior stale/cross-scope/downgrade (fail-closed).</summary>
    WatermarkRejected,
}

/// <summary>Classificação auxiliar de desfechos de execução de delta.</summary>
public static class EvDeltaRunOutcomes
{
    /// <summary>Verdadeiro quando o desfecho é definitivo (sem retry automático da MESMA execução).</summary>
    public static bool IsTerminal(EvDeltaRunOutcome outcome) =>
        outcome is EvDeltaRunOutcome.Completed or EvDeltaRunOutcome.StrategyUnsupported or EvDeltaRunOutcome.WatermarkRejected;

    /// <summary>Verdadeiro quando o desfecho representa um bloqueio ANTES de qualquer chamada ao adapter EV.</summary>
    public static bool IsBlockedBeforeAdapterCall(EvDeltaRunOutcome outcome) =>
        outcome is EvDeltaRunOutcome.StrategyUnsupported or EvDeltaRunOutcome.WatermarkRejected;
}
