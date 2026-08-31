using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>Um critério obrigatório do §49 que impede <see cref="MigrationCompletionOutcome.Eligible"/> (AB-I8-010) — parte do relatório sanitizado (escopo obrigatório item 12).</summary>
public sealed record MigrationCompletionBlocker(MigrationCompletionCriterionId CriterionId, ReadinessControlStatus Status, string ReasonCode);
