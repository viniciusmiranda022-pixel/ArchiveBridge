using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Planning;

/// <summary>
/// Evidência de avaliação de capacidade a ser registrada (tabela <c>planning_assessments</c>).
/// Contém apenas metadados de planejamento — sem conteúdo, segredo, SAS ou token. A liberação de um
/// bloqueio exige responsável e motivo explícitos (nunca override silencioso por configuração).
/// </summary>
public sealed record PlanningAssessment(
    string Mailbox,
    long TotalBytes,
    string RuleCode,
    CapacityAssessmentResult Result,
    string Reason,
    CorrelationId Correlation,
    DateTimeOffset AssessedAtUtc,
    string? ReleasedBy);
