namespace ArchiveBridge.Domain.Canary;

/// <summary>Resultado puro da agregação de <see cref="CanaryGateEvaluator"/> — nunca construído fora dele.</summary>
public sealed record CanaryEvaluation
{
    internal CanaryEvaluation(CanaryOutcome outcome, IReadOnlyList<CanaryScenarioResult> scenarioResults, IReadOnlyList<CanaryBlocker> blockers)
    {
        Outcome = outcome;
        ScenarioResults = scenarioResults;
        Blockers = blockers;
    }

    /// <summary>Desfecho agregado.</summary>
    public CanaryOutcome Outcome { get; }

    /// <summary>Desfecho resolvido de CADA cenário do catálogo, na ordem determinística do catálogo.</summary>
    public IReadOnlyList<CanaryScenarioResult> ScenarioResults { get; }

    /// <summary>Cenários que impedem <see cref="CanaryOutcome.CanaryPassed"/> — vazia se e somente se <see cref="Outcome"/> é <see cref="CanaryOutcome.CanaryPassed"/>.</summary>
    public IReadOnlyList<CanaryBlocker> Blockers { get; }
}
