using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;

/// <summary>
/// Projeção SOMENTE LEITURA (identidade de manutenção) dos escopos com trabalho de UPLOAD Purview
/// elegível — mesmo padrão de <c>IEvExportPendingScopeReader</c>: enumera SOMENTE o par (tenant, projeto),
/// nunca wave/SAS/binding/evidência.
/// </summary>
public interface IPurviewUploadPendingScopeReader
{
    /// <summary>Lista, de forma determinística e limitada a <paramref name="max"/>, os escopos DISTINTOS com pedido de upload elegível.</summary>
    Task<IReadOnlyList<TenantScope>> ListEligibleScopesAsync(int max, CancellationToken cancellationToken);
}
