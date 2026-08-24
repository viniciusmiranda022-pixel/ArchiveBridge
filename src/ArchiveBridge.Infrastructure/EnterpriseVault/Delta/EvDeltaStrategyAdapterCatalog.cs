using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Delta;

/// <summary>
/// Catálogo dos adapters de delta strategy registrados no Connector Host (AB-4C-008 req 7). Resolução
/// determinística por <see cref="EvDeltaStrategyId"/> — ausência de adapter para uma strategy elegível é
/// devolvida como <see langword="null"/> (a Application trata como falha fechada, nunca "melhor esforço").
/// </summary>
public sealed class EvDeltaStrategyAdapterCatalog : IEvDeltaStrategyAdapterCatalog
{
    private readonly Dictionary<EvDeltaStrategyId, IEvDeltaStrategyAdapter> _adapters;

    /// <summary>Constrói o catálogo a partir de TODOS os adapters registrados na composição (DI).</summary>
    public EvDeltaStrategyAdapterCatalog(IEnumerable<IEvDeltaStrategyAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(static adapter => adapter.StrategyId);
    }

    /// <inheritdoc />
    public IEvDeltaStrategyAdapter? Resolve(EvDeltaStrategyId strategyId) =>
        _adapters.TryGetValue(strategyId, out var adapter) ? adapter : null;
}
