namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Classificação FIXA de como um critério de encerramento (§49) pode ser resolvido (AB-I8-010) — nunca
/// informada pelo chamador, sempre derivada de <see cref="MigrationCompletionCriterionCatalog"/> (mesmo
/// princípio de <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlEvidenceSource"/>).
/// </summary>
public enum MigrationCompletionCriterionEvidenceSource : byte
{
    /// <summary>
    /// Resolvido AUTOMATICAMENTE pelo agregador a partir de um store de evidência canônico já existente (ex.:
    /// reconciliation certificate do I6) — atestação manual NUNCA é aceita para um critério desta classe.
    /// </summary>
    SystemDerived = 0,

    /// <summary>
    /// SOMENTE resolvido por atestação manual RBAC'd de um ator autorizado — não existe hoje nenhum store de
    /// evidência automatizado para este critério (ex.: aprovação do cliente, holds/retention revisados pelo
    /// owner). A atestação é, ela própria, a evidência auditável exigida pelo runbook §49 — nunca uma alegação
    /// implícita: <see cref="MigrationCompletionCriterionAttestation.Create"/> exige uma referência de
    /// evidência real e um ator/papel/correlação server-side.
    /// </summary>
    Attested = 1,
}
