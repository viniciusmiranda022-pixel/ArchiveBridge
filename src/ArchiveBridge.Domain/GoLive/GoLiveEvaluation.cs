using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Domain.GoLive;

/// <summary>Resultado puro da agregação de <see cref="GoLiveGateEvaluator"/> — nunca construído fora dele.</summary>
public sealed record GoLiveEvaluation
{
    internal GoLiveEvaluation(
        GoLiveOutcome outcome,
        IReadOnlyList<ReadinessControlResult> operationalControlResults,
        IReadOnlyList<GoLiveBlocker> blockers)
    {
        Outcome = outcome;
        OperationalControlResults = operationalControlResults;
        Blockers = blockers;
    }

    /// <summary>Desfecho agregado.</summary>
    public GoLiveOutcome Outcome { get; }

    /// <summary>
    /// Desfecho resolvido FRESCO, no instante desta decisão, de CADA controle operacional/M365
    /// (§47.4/§47.5 — <see cref="ReadinessGateGroup.Operations"/>/<see cref="ReadinessGateGroup.Microsoft365"/>)
    /// do catálogo do Production Readiness Review (AB-I8-001), na ordem determinística do catálogo.
    /// </summary>
    public IReadOnlyList<ReadinessControlResult> OperationalControlResults { get; }

    /// <summary>Motivos que impedem <see cref="GoLiveOutcome.GoLiveAuthorized"/> — vazia se e somente se <see cref="Outcome"/> é <see cref="GoLiveOutcome.GoLiveAuthorized"/>.</summary>
    public IReadOnlyList<GoLiveBlocker> Blockers { get; }
}
