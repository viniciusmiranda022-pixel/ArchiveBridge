using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Contracts.Approvals;

/// <summary>Tipo do sujeito de uma decisão de aprovação (persistido como TINYINT).</summary>
public enum ApprovalSubjectType
{
    /// <summary>Projeto de migração.</summary>
    Project = 0,

    /// <summary>Onda de migração.</summary>
    Wave = 1,

    /// <summary>Versão de mapping.</summary>
    Mapping = 2,
}

/// <summary>Decisão registrada (persistida como TINYINT).</summary>
public enum ApprovalDecision
{
    /// <summary>Aprovado.</summary>
    Approved = 0,

    /// <summary>Rejeitado.</summary>
    Rejected = 1,
}

/// <summary>
/// Registro imutável de uma decisão de aprovação (linha da tabela <c>approvals</c>), gravado
/// atomicamente junto da transição do agregado. Liga a decisão ao sujeito e à sua VERSÃO corrente
/// (uma decisão nunca se aplica a uma versão diferente). Notas são livres de PII.
/// </summary>
public sealed record ApprovalRecord(
    ApprovalSubjectType SubjectType,
    Guid SubjectId,
    int SubjectVersion,
    ApprovalDecision Decision,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    CorrelationId Correlation,
    string? Notes);
