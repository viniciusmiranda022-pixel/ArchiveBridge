using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview;

/// <summary>Resultado de <see cref="ICapabilityEvidenceStore.AppendAsync"/> — <see cref="Created"/> falso indica réplay idempotente.</summary>
public sealed record CapabilityEvidenceAppendResult(CapabilityEvidence Evidence, bool Created);

/// <summary>
/// Store append-only de <see cref="CapabilityEvidence"/>, escopado a tenant/projeto/provedor/rota. Nenhuma
/// linha é atualizada ou removida — <see cref="GetLatestAsync"/> sempre lê a evidência vigente (mais
/// recente por <see cref="CapabilityEvidence.Version"/>) diretamente do histórico completo.
/// </summary>
public interface ICapabilityEvidenceStore
{
    /// <summary>Devolve a evidência vigente (mais recente) dentro do escopo; <see langword="null"/> se nenhuma existir.</summary>
    Task<CapabilityEvidence?> GetLatestAsync(
        TenantScope scope, TargetProvider provider, PurviewCapabilityRoute route, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste uma nova versão (append). Se a versão candidata já foi ocupada por outra submissão
    /// concorrente com o MESMO conteúdo lógico, converge (<see cref="CapabilityEvidenceAppendResult.Created"/>
    /// = <see langword="false"/>); com conteúdo diferente, lança <see cref="ArchiveBridge.Domain.Common.ConcurrencyException"/>.
    /// </summary>
    Task<CapabilityEvidenceAppendResult> AppendAsync(CapabilityEvidence evidence, CancellationToken cancellationToken);
}
