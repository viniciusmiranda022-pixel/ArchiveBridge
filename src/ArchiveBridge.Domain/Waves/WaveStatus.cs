namespace ArchiveBridge.Domain.Waves;

/// <summary>
/// Estados do ciclo de vida de uma <see cref="MigrationWave"/>. Transições permitidas em
/// <see cref="WaveStateMachine"/>; inválidas falham de forma fechada.
/// </summary>
public enum WaveStatus
{
    /// <summary>Rascunho: seleção e destino mutáveis.</summary>
    Draft,

    /// <summary>Em validação (capacidade, mapping, isolamento).</summary>
    Validating,

    /// <summary>Bloqueada (ex.: capacidade &gt; 100 GB) até evidência/decisão.</summary>
    Blocked,

    /// <summary>Validações concluídas; pronta para aprovação.</summary>
    ReadyForApproval,

    /// <summary>Aprovada: seleção, destino e pasta congelados.</summary>
    Approved,

    /// <summary>Congelada para execução (imutável).</summary>
    Frozen,

    /// <summary>Concluída (terminal).</summary>
    Completed,

    /// <summary>Cancelada (terminal).</summary>
    Cancelled,
}
