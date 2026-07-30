using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>
/// Reivindica atomicamente o próximo Job elegível de um workload para um worker. A garantia de
/// vencedor único e o incremento da época do lease são responsabilidade da porta durável.
/// </summary>
public sealed class ClaimNextJobUseCase(IJobStore store)
{
    private readonly IJobStore _store = store;

    /// <summary>Reivindica o próximo Job; <see langword="null"/> quando não há trabalho elegível.</summary>
    public Task<ClaimedJob?> ExecuteAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _store.TryClaimNextAsync(request, cancellationToken);
    }
}
