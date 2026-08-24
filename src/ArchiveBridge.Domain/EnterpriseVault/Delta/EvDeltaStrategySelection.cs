namespace ArchiveBridge.Domain.EnterpriseVault.Delta;

/// <summary>Desfecho da seleção determinística de delta strategy (AB-4C-008 req 2/8) — fail-closed em toda ambiguidade.</summary>
public enum EvDeltaStrategySelectionOutcome
{
    /// <summary>Versão EV não reconhecida por nenhuma família da matriz — nunca inferida (req 2).</summary>
    Unknown,

    /// <summary>Família reconhecida, mas nenhuma strategy elegível para a fase pedida (vetada ou abaixo de Compatible).</summary>
    Unsupported,

    /// <summary>Exatamente uma strategy elegível vence de forma determinística.</summary>
    Supported,

    /// <summary>Mais de uma strategy elegível empatada na maior precedência — falha fechada, nunca escolha arbitrária.</summary>
    Ambiguous,
}

/// <summary>Resultado da seleção: o desfecho, a strategy escolhida (quando <see cref="EvDeltaStrategySelectionOutcome.Supported"/>) e os candidatos avaliados.</summary>
public sealed record EvDeltaStrategySelection(
    EvDeltaStrategySelectionOutcome Outcome,
    EvDeltaStrategyDescriptor? Selected,
    IReadOnlyList<EvDeltaStrategyDescriptor> Candidates);

/// <summary>
/// Política de seleção DETERMINÍSTICA de delta strategy (AB-4C-008 req 2/8), espelhando
/// <see cref="Discovery.AdapterSelectionPolicy"/>: nenhuma strategy implícita para versão/schema
/// desconhecida — o desfecho é sempre <see cref="EvDeltaStrategySelectionOutcome.Unknown"/>,
/// <see cref="EvDeltaStrategySelectionOutcome.Unsupported"/> ou <see cref="EvDeltaStrategySelectionOutcome.Ambiguous"/>
/// antes de qualquer suposição, nunca um "melhor esforço".
/// </summary>
public static class EvDeltaStrategySelectionPolicy
{
    /// <summary>Aplica a política determinística à versão EV observada e à fase pedida (consulta a matriz embarcada).</summary>
    public static EvDeltaStrategySelection Select(string evVersionDisplay, EvDeltaPhase phase) =>
        SelectFrom(EvDeltaStrategyCatalog.Evaluate(evVersionDisplay), phase, treatEmptyAsUnknown: true);

    /// <summary>
    /// Aplica a política determinística a uma lista de candidatos já avaliada (uso interno/testes — mesmo
    /// desenho de <see cref="Discovery.AdapterSelectionPolicy.Select"/>, que também opera sobre avaliações
    /// já produzidas em vez de reconsultar a fonte).
    /// </summary>
    public static EvDeltaStrategySelection SelectFrom(IReadOnlyList<EvDeltaStrategyDescriptor> candidates, EvDeltaPhase phase) =>
        SelectFrom(candidates, phase, treatEmptyAsUnknown: false);

    private static EvDeltaStrategySelection SelectFrom(IReadOnlyList<EvDeltaStrategyDescriptor> candidates, EvDeltaPhase phase, bool treatEmptyAsUnknown)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 && treatEmptyAsUnknown)
        {
            return new EvDeltaStrategySelection(EvDeltaStrategySelectionOutcome.Unknown, Selected: null, candidates);
        }

        var eligible = candidates.Where(descriptor => descriptor.IsEligibleFor(phase)).ToArray();
        if (eligible.Length == 0)
        {
            return new EvDeltaStrategySelection(EvDeltaStrategySelectionOutcome.Unsupported, Selected: null, candidates);
        }

        var maxPrecedence = eligible.Max(descriptor => descriptor.Precedence);
        var top = eligible.Where(descriptor => descriptor.Precedence == maxPrecedence).ToArray();
        if (top.Length > 1)
        {
            // Empate de precedência entre elegíveis: a política não distingue por mérito — falha fechada.
            return new EvDeltaStrategySelection(EvDeltaStrategySelectionOutcome.Ambiguous, Selected: null, candidates);
        }

        return new EvDeltaStrategySelection(EvDeltaStrategySelectionOutcome.Supported, top[0], candidates);
    }
}
