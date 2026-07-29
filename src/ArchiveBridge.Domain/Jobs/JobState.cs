namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Estados do ciclo de vida durável de um Job (Vertical Slice 1). Conjunto mínimo:
/// Pending → Processing → (Completed | Failed | RetryScheduled) e Cancelled a partir de
/// qualquer estado não terminal. As transições permitidas são definidas em
/// <see cref="JobStateMachine"/>; transições inválidas falham de forma fechada.
/// </summary>
public enum JobState
{
    /// <summary>Aguardando reivindicação (elegível quando <c>NextAttemptAt</c> venceu).</summary>
    Pending,

    /// <summary>Reivindicado por um worker sob lease (owner_worker + lease_epoch).</summary>
    Processing,

    /// <summary>Falha transitória: reagendado para nova tentativa em <c>NextAttemptAt</c>.</summary>
    RetryScheduled,

    /// <summary>Concluído com sucesso (terminal).</summary>
    Completed,

    /// <summary>Falha definitiva ou tentativas esgotadas (terminal).</summary>
    Failed,

    /// <summary>Cancelado por decisão de controle (terminal).</summary>
    Cancelled,
}
