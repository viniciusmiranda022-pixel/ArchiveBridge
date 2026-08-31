namespace ArchiveBridge.Domain.Canary;

/// <summary>
/// Classificação FIXA de como um cenário do catálogo pode ser resolvido (AB-I8-004) — nunca informada pelo
/// chamador, sempre derivada de <see cref="CanaryScenarioCatalog"/> (mesmo princípio de defesa de
/// <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlEvidenceSource"/>).
/// </summary>
public enum CanaryScenarioEvidenceSource : byte
{
    /// <summary>
    /// Resolvido AUTOMATICAMENTE a partir de um store de evidência canônico já existente (I5/I6/I7) —
    /// atestação manual de operador NUNCA é aceita para um cenário desta classe.
    /// </summary>
    SystemDerived = 0,

    /// <summary>
    /// SOMENTE resolvido por atestação de operador RBAC'd, com referência de evidência opaca — nenhum store
    /// automatizado dedicado existe hoje para observar este cenário (ex.: diversidade de tipos de item do
    /// corpus, cobertura de boundary de tamanho de PST), mesmo princípio de
    /// <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlEvidenceSource.Attested"/>.
    /// </summary>
    OperatorAttested = 1,

    /// <summary>
    /// O gate de decisão humana final (AB-I8-004 escopo obrigatório item 11) — resolvido EXCLUSIVAMENTE por
    /// <c>ApproveCanaryFirstWaveUseCase</c>, nunca pela submissão genérica de evidência de operador. Existe
    /// no MÁXIMO um cenário desta classe no catálogo (<see cref="CanaryScenarioCatalog.FirstWaveApprovalScenarioId"/>).
    /// </summary>
    ApprovalDecision = 2,
}
