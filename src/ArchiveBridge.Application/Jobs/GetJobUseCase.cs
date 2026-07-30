using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>Lê o estado durável de um Job dentro do escopo de tenant/projeto (RLS aplicada).</summary>
public sealed class GetJobUseCase(IJobStore store)
{
    private readonly IJobStore _store = store;

    /// <summary>Retorna o snapshot do Job; <see langword="null"/> se inexistente ou de outro tenant.</summary>
    public Task<JobSnapshot?> ExecuteAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken) =>
        _store.GetAsync(scope, jobId, cancellationToken);
}
