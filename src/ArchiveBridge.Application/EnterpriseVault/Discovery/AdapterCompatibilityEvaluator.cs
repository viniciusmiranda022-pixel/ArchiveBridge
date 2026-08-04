using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>
/// Executa TODOS os adapters registrados contra uma observação e aplica a
/// <see cref="AdapterSelectionPolicy"/> determinística. Os adapters são ordenados por identidade
/// (estável) antes da avaliação, de modo que a lista de candidatos e o desempate sejam reprodutíveis.
/// A seleção é por CAPACIDADES, nunca pela versão textual do Enterprise Vault.
/// </summary>
public sealed class AdapterCompatibilityEvaluator(IEnumerable<IEvVersionAdapter> adapters)
{
    private readonly IReadOnlyList<IEvVersionAdapter> _adapters = adapters
        .OrderBy(static adapter => adapter.AdapterId.Value, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Avalia a observação com todos os adapters e devolve a seleção determinística.</summary>
    public EvAdapterSelection Select(EvDiscoveryObservation observation, EvDiscoveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(policy);
        var evaluations = _adapters
            .Select(adapter => adapter.Evaluate(observation, policy))
            .ToArray();
        return AdapterSelectionPolicy.Select(evaluations);
    }
}
