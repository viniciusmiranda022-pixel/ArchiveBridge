using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Planning;

/// <summary>
/// Evidência de avaliação de capacidade a ser registrada (tabela <c>planning_assessments</c>).
/// Contém apenas metadados de planejamento — sem conteúdo, segredo, SAS ou token. O archive é
/// identificado pela sua <see cref="TargetArchiveId"/> canônica (a chave de agrupamento), não pela
/// string de exibição. A liberação de um bloqueio exige responsável e motivo explícitos (nunca
/// override silencioso por configuração).
/// </summary>
public sealed record PlanningAssessment(
    TargetArchiveId Archive,
    long TotalBytes,
    string RuleCode,
    CapacityAssessmentResult Result,
    string Reason,
    CorrelationId Correlation,
    DateTimeOffset AssessedAtUtc,
    string? ReleasedBy);
