namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Desfecho agregado de UMA avaliação de encerramento de migração (AB-I8-010, runbook §49). DELIBERADAMENTE
/// possui apenas estes dois valores — não existe, e nunca deve existir, um caso <c>Completed</c>
/// (STOP-THE-LINE do work order: este gate NUNCA marca a migração/projeto/wave concluído; apenas determina se
/// TODOS os critérios documentados do §49 estão satisfeitos, o que continua sendo uma pré-condição necessária
/// mas não uma execução de encerramento). <see cref="Blocked"/> é o default fail-closed (valor 0).
/// </summary>
public enum MigrationCompletionOutcome : byte
{
    /// <summary>Ao menos um dos onze critérios obrigatórios do §49 não está <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlStatus.Pass"/> — fail-closed default.</summary>
    Blocked = 0,

    /// <summary>
    /// TODOS os onze critérios obrigatórios do §49 estão <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlStatus.Pass"/>.
    /// Mesmo neste estado, este tipo NUNCA representa a migração <c>Completed</c> por si só — apenas que os
    /// critérios documentados do runbook §49 estão, no instante desta avaliação, integralmente satisfeitos por
    /// evidência canônica.
    /// </summary>
    Eligible = 1,
}
