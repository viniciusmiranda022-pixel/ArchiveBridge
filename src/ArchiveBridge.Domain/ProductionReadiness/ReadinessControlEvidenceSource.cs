namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Classificação FIXA de como um controle do catálogo pode ser resolvido (AB-I8-001) — nunca informada
/// pelo chamador, sempre derivada de <see cref="ReadinessControlCatalog"/> (mesmo princípio de defesa contra
/// "privilege spoofing" de <see cref="ArchiveBridge.Domain.Security.WorkerHardeningBaselineCatalog"/>).
/// </summary>
public enum ReadinessControlEvidenceSource : byte
{
    /// <summary>
    /// Resolvido AUTOMATICAMENTE pelo agregador a partir de um store de evidência canônico já existente de
    /// um incremento anterior (I6/I7) ou de uma invariante de código verificada em runtime — atestação
    /// manual NUNCA é aceita para um controle desta classe (bloqueio estrutural: garante que pen-test/RTO/
    /// RPO/capability nunca sejam "aprovados" por alegação humana).
    /// </summary>
    SystemDerived = 0,

    /// <summary>
    /// SOMENTE resolvido por atestação manual RBAC'd de um ator autorizado — não existe hoje nenhum store de
    /// evidência automatizado para este controle (ex.: ADR aprovado, capacity/FinOps).
    /// </summary>
    Attested = 1,
}
