namespace ArchiveBridge.Domain.EnterpriseVault.Export;

/// <summary>
/// Desfecho TERMINAL de uma tentativa de exportação (AB-4C-005). Cada tentativa produz exatamente um
/// desfecho, persistido de forma append-only — a história de tentativas nunca é reescrita, apenas
/// estendida. <see cref="Throttled"/> e <see cref="CapabilityBlocked"/> NUNCA chegam a invocar o
/// executor/processo (bloqueados antes de qualquer efeito externo).
/// </summary>
public enum EvExportAttemptOutcome
{
    /// <summary>Concluída com sucesso: outputs descobertos, hasheados e o manifesto canônico persistido.</summary>
    Completed,

    /// <summary>O processo do exporter falhou (exit code, timeout ou saída inválida).</summary>
    Failed,

    /// <summary>Bloqueada antes de executar: capability de exportação ausente/revogada (fail-closed).</summary>
    CapabilityBlocked,

    /// <summary>Bloqueada antes de executar: concorrência não autorizada no mesmo connector/archive.</summary>
    Throttled,

    /// <summary>Concluída pelo processo, mas a revalidação dos outputs físicos falhou (fail-closed).</summary>
    IntegrityFailed,
}

/// <summary>Indica se o desfecho é um efeito de negócio elegível a persistência de attempt (nunca Throttled).</summary>
public static class EvExportAttemptOutcomes
{
    /// <summary>Verdadeiro para desfechos que representam uma tentativa REAL de execução (não apenas bloqueio de fila).</summary>
    public static bool IsRecordable(EvExportAttemptOutcome outcome) => true; // todo desfecho é auditável/registrável.

    /// <summary>Verdadeiro quando o desfecho é definitivo (sem retry automático subsequente).</summary>
    public static bool IsTerminal(EvExportAttemptOutcome outcome) =>
        outcome is EvExportAttemptOutcome.Completed or EvExportAttemptOutcome.CapabilityBlocked
            or EvExportAttemptOutcome.IntegrityFailed;
}
