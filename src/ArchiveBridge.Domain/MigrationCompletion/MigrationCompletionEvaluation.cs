namespace ArchiveBridge.Domain.MigrationCompletion;

/// <summary>Resultado puro da agregação de <see cref="MigrationCompletionGateEvaluator"/> — nunca construído fora dele.</summary>
public sealed record MigrationCompletionEvaluation
{
    internal MigrationCompletionEvaluation(
        MigrationCompletionOutcome outcome,
        IReadOnlyList<MigrationCompletionCriterionResult> criterionResults,
        IReadOnlyList<MigrationCompletionBlocker> blockers)
    {
        Outcome = outcome;
        CriterionResults = criterionResults;
        Blockers = blockers;
    }

    /// <summary>Desfecho agregado.</summary>
    public MigrationCompletionOutcome Outcome { get; }

    /// <summary>Desfecho resolvido de CADA critério do catálogo (§49), na ordem determinística do catálogo.</summary>
    public IReadOnlyList<MigrationCompletionCriterionResult> CriterionResults { get; }

    /// <summary>Critérios que impedem <see cref="MigrationCompletionOutcome.Eligible"/> — vazia se e somente se <see cref="Outcome"/> é <see cref="MigrationCompletionOutcome.Eligible"/>.</summary>
    public IReadOnlyList<MigrationCompletionBlocker> Blockers { get; }
}
