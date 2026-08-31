namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>
/// Classificação FIXA de como um critério de encerramento (§49) pode ser resolvido (AB-I8-010, correção
/// obrigatória AB-I8-011) — nunca informada pelo chamador, sempre derivada de
/// <see cref="MigrationCompletionCriterionCatalog"/> (mesmo princípio de
/// <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlEvidenceSource"/>). Um <c>Attested</c>
/// genérico masacararia a diferença entre um critério genuinamente procedural (decisão humana, sem verdade
/// técnica objetiva) e um critério tecnicamente objetivo que só ainda não possui store canônico suficiente
/// neste repositório (AB-I8-011) — por isso existem TRÊS classes, nunca duas.
/// </summary>
public enum MigrationCompletionCriterionEvidenceSource : byte
{
    /// <summary>
    /// Resolvido AUTOMATICAMENTE pelo agregador a partir de um store de evidência canônico já existente e
    /// SUFICIENTE (ex.: reconciliation certificate do I6) — atestação manual NUNCA é aceita para um critério
    /// desta classe.
    /// </summary>
    SystemDerived = 0,

    /// <summary>
    /// A verdade deste critério é TÉCNICA/OBJETIVA (não uma opinião/decisão humana) e por isso DEVE ser
    /// resolvida automaticamente a partir de evidência canônica real — atestação manual NUNCA é aceita para um
    /// critério desta classe, pelo mesmo bloqueio estrutural de <see cref="SystemDerived"/>
    /// (<see cref="MigrationCompletionCriterionAttestation.RequireAttestable"/> recusa ambas as classes).
    /// Diferente de <see cref="SystemDerived"/>: este repositório ainda NÃO possui um store canônico
    /// suficiente para resolver este critério (AB-I8-011) — a resolução automática correta e fail-closed é,
    /// portanto, permanentemente <see cref="ArchiveBridge.Domain.ProductionReadiness.ReadinessControlStatus.NotMeasured"/>
    /// até que um store real seja implementado em um slice futuro; nenhum resolver parcial/heurístico é
    /// aceitável (arriscaria produzir confiança falsa), e uma alegação humana nunca pode substituir a ausência
    /// desse store (STOP-THE-LINE de AB-I8-011: "não inventar novos thresholds, stores, estados ou
    /// evidências").
    /// </summary>
    EvidenceDerived = 2,

    /// <summary>
    /// A verdade deste critério é GENUINAMENTE PROCESSUAL/de decisão humana (ex.: aprovação do cliente, revisão
    /// de holds/retention pelo owner) — não existe hoje, nem faria sentido conceitual, um store automatizado
    /// que a substitua. SOMENTE resolvido por atestação manual RBAC'd de um ator autorizado. A atestação é, ela
    /// própria, a evidência auditável exigida pelo runbook §49 — nunca uma alegação implícita:
    /// <see cref="MigrationCompletionCriterionAttestation.Create"/> exige uma referência de evidência real e um
    /// ator/papel/correlação server-side.
    /// </summary>
    HumanApproval = 1,
}
