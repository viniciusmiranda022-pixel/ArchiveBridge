namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>
/// Fase de UMA operação lógica de migração de archive (runbook §16.5, passos 27-32; AB-4C-008 req 1).
/// Cada fase é explícita e auditável — nunca inferida implicitamente do estado de outra fase.
/// </summary>
public enum EvDeltaPhase
{
    /// <summary>Carga inicial completa do archive (passo 27); estabelece o primeiro watermark canônico.</summary>
    Baseline,

    /// <summary>Carga incremental subsequente, sempre a partir do último watermark canônico aceito (passo 30).</summary>
    Delta,

    /// <summary>
    /// Delta final, executado somente após freeze formalmente autorizado, antecedendo o cutover (passo 32).
    /// Nunca elegível sem <see cref="EvFreezeStatus.FreezeAuthorized"/> persistido.
    /// </summary>
    FinalDelta,
}
