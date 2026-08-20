namespace ArchiveBridge.Domain.PstProcessing;

/// <summary>
/// Desfecho de UMA tentativa de inspeção (persistido como <c>TINYINT</c>, mesmo valor numérico no banco).
/// Distinto do diagnóstico estrutural do PST em si: <see cref="Completed"/> só significa que a engine
/// concluiu a leitura — o PST pode ainda assim ser diagnosticado como inválido/corrompido (ver
/// <see cref="PstStructuralDiagnostic"/>). <see cref="Stale"/> e <see cref="LimitExceeded"/> nunca produzem
/// resultado canônico reutilizável — são registrados apenas para evidência/auditoria (fail-closed).
/// </summary>
public enum PstInspectionOutcome
{
    /// <summary>A engine abriu e leu o artefato até uma conclusão estrutural definitiva.</summary>
    Completed = 0,

    /// <summary>O hash observado no momento da inspeção diverge do hash registrado em custódia.</summary>
    Stale = 1,

    /// <summary>Limite de tamanho/tempo/recursos excedido; inspeção interrompida fail-closed.</summary>
    LimitExceeded = 2,
}
