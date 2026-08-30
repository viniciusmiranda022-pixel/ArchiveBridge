namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>Origem da evidência referenciada por um <see cref="ReadinessEvidenceReference"/>.</summary>
public enum ReadinessEvidenceKind : byte
{
    /// <summary>Nenhuma evidência foi produzida ainda — fail-closed default.</summary>
    None = 0,

    /// <summary>
    /// Evidência resolvida automaticamente pelo agregador a partir de um store canônico já existente de
    /// um incremento anterior (I6/I7) ou de uma invariante de código verificada em runtime — nunca de um
    /// valor alegado pelo chamador.
    /// </summary>
    SystemDerived = 1,

    /// <summary>
    /// Atestação manual RBAC'd de um ator autorizado, para controles processuais/documentais que ainda não
    /// possuem um store de evidência automatizado (ex.: ADR aprovado, diagrama atualizado).
    /// </summary>
    ManualAttestation = 2,
}
