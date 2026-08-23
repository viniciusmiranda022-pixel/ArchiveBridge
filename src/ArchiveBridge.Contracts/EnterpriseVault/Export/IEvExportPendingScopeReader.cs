using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Contracts.EnterpriseVault.Export;

/// <summary>
/// Projeção SOMENTE LEITURA (identidade de manutenção) dos escopos com trabalho de EXPORTAÇÃO EV elegível —
/// mesmo padrão de <c>IEvDiscoveryPendingScopeReader</c> (Slice 3/4A), nunca uma regra paralela: enumera
/// SOMENTE o par (tenant, projeto), nunca connector/archive/política/evidência.
/// </summary>
public interface IEvExportPendingScopeReader
{
    /// <summary>Lista, de forma determinística e limitada a <paramref name="max"/>, os escopos DISTINTOS com pedido de exportação elegível.</summary>
    Task<IReadOnlyList<TenantScope>> ListEligibleScopesAsync(int max, CancellationToken cancellationToken);
}
