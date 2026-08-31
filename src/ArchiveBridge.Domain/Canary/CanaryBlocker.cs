namespace ArchiveBridge.Domain.Canary;

/// <summary>Um cenário obrigatório que impede <see cref="CanaryOutcome.CanaryPassed"/> (AB-I8-004).</summary>
public sealed record CanaryBlocker(CanaryScenarioId ScenarioId, CanaryScenarioStatus Status, string ReasonCode);
