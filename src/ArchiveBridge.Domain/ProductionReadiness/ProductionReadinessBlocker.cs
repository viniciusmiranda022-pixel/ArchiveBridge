namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>Um controle obrigatório que impede <see cref="ProductionReadinessOutcome.ReadyForCanary"/> — parte do relatório sanitizado (AB-I8-001 escopo item 8).</summary>
public sealed record ProductionReadinessBlocker(
    ReadinessControlId ControlId,
    ReadinessGateGroup Group,
    ReadinessControlStatus Status,
    string ReasonCode);
