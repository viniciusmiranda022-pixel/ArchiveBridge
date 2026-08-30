namespace ArchiveBridge.Domain.ProductionReadiness;

/// <summary>
/// Resultado PURO da agregação dos controles do catálogo (AB-I8-001) — saída de
/// <see cref="ProductionReadinessGateEvaluator.Evaluate"/>, consumida por
/// <see cref="ProductionReadinessReviewSnapshot.Compose"/> para materializar o snapshot imutável.
/// </summary>
public sealed record ProductionReadinessEvaluation
{
    internal ProductionReadinessEvaluation(
        ProductionReadinessOutcome outcome,
        IReadOnlyList<ReadinessControlResult> controlResults,
        IReadOnlyList<ProductionReadinessBlocker> blockers)
    {
        Outcome = outcome;
        ControlResults = controlResults;
        Blockers = blockers;
    }

    /// <summary>Desfecho agregado — <see cref="ProductionReadinessOutcome.ReadyForCanary"/> somente quando <see cref="Blockers"/> está vazio.</summary>
    public ProductionReadinessOutcome Outcome { get; }

    /// <summary>Desfecho resolvido de CADA controle do catálogo, na ordem determinística do catálogo (§47.1 a §47.5) — inclui controles sintetizados como <see cref="ReadinessControlStatus.NotMeasured"/> quando ausentes da evidência fornecida.</summary>
    public IReadOnlyList<ReadinessControlResult> ControlResults { get; }

    /// <summary>Todo controle obrigatório que não está <see cref="ReadinessControlStatus.Pass"/> — vazio se e somente se <see cref="Outcome"/> é <see cref="ProductionReadinessOutcome.ReadyForCanary"/>.</summary>
    public IReadOnlyList<ProductionReadinessBlocker> Blockers { get; }
}
