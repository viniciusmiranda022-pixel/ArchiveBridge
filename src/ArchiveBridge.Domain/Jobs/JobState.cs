namespace ArchiveBridge.Domain.Jobs;

/// <summary>Estados de job da fila durável (ADR-0003). Efeito externo nunca volta sozinho a Pending.</summary>
public enum JobState
{
    Pending,
    Processing,
    RecoveryRequired,
    Reconciling,
    Completed,
    Failed,
    DeadLetter,
}
