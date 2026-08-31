namespace ArchiveBridge.Domain.Canary;

/// <summary>Origem da evidência de UM resultado de cenário do canário (AB-I8-004). Persistido como <c>TINYINT</c>.</summary>
public enum CanaryEvidenceKind : byte
{
    /// <summary>Nenhuma evidência foi produzida ainda — fail-closed default.</summary>
    None = 0,

    /// <summary>Resolvida automaticamente pelo agregador a partir de um store canônico já existente.</summary>
    SystemDerived = 1,

    /// <summary>Atestação livre de um operador autorizado, com referência de evidência opaca.</summary>
    OperatorAttestation = 2,

    /// <summary>Decisão humana auditável de aprovação da primeira onda real (escopo obrigatório item 11) — nunca sintetizada por resolução automática.</summary>
    HumanApprovalDecision = 3,
}
