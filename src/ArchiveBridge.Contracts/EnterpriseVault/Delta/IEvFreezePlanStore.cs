using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;

namespace ArchiveBridge.Contracts.EnterpriseVault.Delta;

/// <summary>
/// Store do plano de freeze/cutover de UM archive (AB-4C-008 req 9-11). Persiste SOMENTE estado e
/// autorização — nenhuma implementação desta porta pode acionar uma ação real no Enterprise Vault.
/// </summary>
public interface IEvFreezePlanStore
{
    /// <summary>Devolve o plano do archive no escopo; <see langword="null"/> se nenhum tiver sido solicitado ainda.</summary>
    Task<EvFreezePlan?> GetAsync(TenantScope scope, ConnectorId connector, string externalArchiveId, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste o plano sob controle de concorrência OTIMISTA pela versão anterior à transição aplicada em
    /// memória (<paramref name="expectedPreviousVersion"/>): uma versão divergente indica alteração
    /// concorrente e lança <see cref="ArchiveBridge.Domain.Common.ConcurrencyException"/> — fail-closed,
    /// nunca sobrescreve silenciosamente uma autorização/estado concorrente.
    /// </summary>
    /// <exception cref="ArchiveBridge.Domain.Common.ConcurrencyException">A versão esperada não corresponde à persistida (retriable).</exception>
    Task SaveAsync(TenantScope scope, EvFreezePlan plan, int expectedPreviousVersion, CancellationToken cancellationToken);
}
