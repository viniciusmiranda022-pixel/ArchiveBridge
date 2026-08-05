namespace ArchiveBridge.Domain.EnterpriseVault.Discovery;

/// <summary>Desfecho da seleção de adapter por capacidades (fail-closed em ausência/ambiguidade).</summary>
public enum AdapterSelectionOutcome
{
    /// <summary>Exatamente um adapter compatível vence de forma determinística.</summary>
    Supported,

    /// <summary>Nenhum compatível, mas algum adapter reconhece o ambiente como bloqueado (recuperável).</summary>
    Blocked,

    /// <summary>Adapters reconhecem o ambiente, mas nenhum o suporta (não recuperável sem novo adapter).</summary>
    Unsupported,

    /// <summary>Nenhum adapter reconheceu o ambiente (nenhum candidato).</summary>
    NotFound,

    /// <summary>Mais de um adapter compatível empatado na maior precedência — falha fechada.</summary>
    Ambiguous,
}

/// <summary>
/// Resultado da política de seleção: o desfecho, o adapter selecionado (quando <see cref="AdapterSelectionOutcome.Supported"/>),
/// os candidatos considerados e os achados que sustentam a conclusão.
/// </summary>
public sealed record EvAdapterSelection(
    AdapterSelectionOutcome Outcome,
    EvAdapterEvaluation? Selected,
    IReadOnlyList<EvAdapterId> Candidates,
    IReadOnlyList<EvAdapterEvaluation> Evaluations,
    IReadOnlyList<EvDiscoveryFinding> Findings);

/// <summary>
/// Política de seleção de adapter DETERMINÍSTICA e fail-closed sobre as avaliações dos adapters:
/// <list type="bullet">
/// <item>nenhuma avaliação ⇒ <see cref="AdapterSelectionOutcome.NotFound"/>;</item>
/// <item>1 compatível ⇒ <see cref="AdapterSelectionOutcome.Supported"/> (selecionado);</item>
/// <item>vários compatíveis com uma ÚNICA maior precedência ⇒ Supported (o de maior precedência, registrado);</item>
/// <item>vários compatíveis EMPATADOS na maior precedência ⇒ <see cref="AdapterSelectionOutcome.Ambiguous"/> (fail-closed);</item>
/// <item>nenhum compatível, algum Blocked ⇒ <see cref="AdapterSelectionOutcome.Blocked"/>;</item>
/// <item>todos Unsupported ⇒ <see cref="AdapterSelectionOutcome.Unsupported"/>.</item>
/// </list>
/// A seleção NUNCA usa a versão textual do Enterprise Vault; usa a compatibilidade declarada por capacidades.
/// </summary>
public static class AdapterSelectionPolicy
{
    /// <summary>Aplica a política determinística às avaliações e devolve a seleção com evidência.</summary>
    public static EvAdapterSelection Select(IReadOnlyList<EvAdapterEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        // Invariante de unicidade (alinhada a UQ_eveval_adapter): no máximo uma avaliação por AdapterId, fail-closed.
        EvDiscoveryInvariants.EnsureUniqueEvaluations(evaluations);
        var candidates = evaluations
            .Select(static evaluation => evaluation.AdapterId)
            .ToArray();
        var findings = evaluations.SelectMany(static evaluation => evaluation.Findings).ToArray();

        if (evaluations.Count == 0)
        {
            return new EvAdapterSelection(AdapterSelectionOutcome.NotFound, Selected: null, candidates, evaluations, findings);
        }

        var supported = evaluations
            .Where(static evaluation => evaluation.Compatibility == AdapterCompatibility.Supported)
            .ToArray();

        if (supported.Length == 0)
        {
            var outcome = evaluations.Any(static evaluation => evaluation.Compatibility == AdapterCompatibility.Blocked)
                ? AdapterSelectionOutcome.Blocked
                : AdapterSelectionOutcome.Unsupported;
            return new EvAdapterSelection(outcome, Selected: null, candidates, evaluations, findings);
        }

        var maxPrecedence = supported.Max(static evaluation => evaluation.Precedence);
        var top = supported.Where(evaluation => evaluation.Precedence == maxPrecedence).ToArray();
        if (top.Length > 1)
        {
            // Empate de precedência entre compatíveis: a política não distingue por mérito — falha fechada.
            return new EvAdapterSelection(AdapterSelectionOutcome.Ambiguous, Selected: null, candidates, evaluations, findings);
        }

        return new EvAdapterSelection(AdapterSelectionOutcome.Supported, top[0], candidates, evaluations, findings);
    }
}
