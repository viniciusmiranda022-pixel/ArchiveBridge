namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Motivo estruturado de uma transição de estado, registrado na trilha de auditoria
/// (<c>job_state_transitions.reason_code</c>). Não contém conteúdo sensível.
/// </summary>
public enum ReasonCode
{
    /// <summary>Job criado (Pending).</summary>
    Created,

    /// <summary>Job reivindicado por um worker (Pending/RetryScheduled → Processing).</summary>
    Claimed,

    /// <summary>Job concluído com sucesso (Processing → Completed).</summary>
    Completed,

    /// <summary>Nova tentativa agendada após falha transitória (Processing → RetryScheduled).</summary>
    RetryScheduled,

    /// <summary>Falha definitiva (Processing → Failed).</summary>
    Failed,

    /// <summary>Cancelado por decisão de controle (→ Cancelled).</summary>
    Cancelled,

    /// <summary>Lease expirado recuperado pelo reaper (Processing → RetryScheduled).</summary>
    LeaseExpiredRecovered,

    /// <summary>Tentativas esgotadas ao recuperar lease expirado (Processing → Failed).</summary>
    AttemptsExhausted,
}
