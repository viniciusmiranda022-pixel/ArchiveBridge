namespace ArchiveBridge.Domain.Projects;

/// <summary>
/// Estados do ciclo de vida de um <see cref="MigrationProject"/>. As transições permitidas são
/// definidas em <see cref="ProjectStateMachine"/>; transições inválidas falham de forma fechada.
/// </summary>
public enum ProjectStatus
{
    /// <summary>Rascunho: configuração mutável, ainda não submetida a avaliação.</summary>
    Draft,

    /// <summary>Pronto para avaliação (assessment de capacidade/segurança).</summary>
    ReadyForAssessment,

    /// <summary>Bloqueado por avaliação (ex.: capacidade &gt; 100 GB) até evidência/decisão.</summary>
    Blocked,

    /// <summary>Aprovado pelo Decision Owner; configuração congelada.</summary>
    Approved,

    /// <summary>Ativo (ondas em execução).</summary>
    Active,

    /// <summary>Concluído (terminal).</summary>
    Completed,

    /// <summary>Cancelado (terminal).</summary>
    Cancelled,
}
