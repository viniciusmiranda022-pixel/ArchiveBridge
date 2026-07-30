using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>Cria um Job durável em Pending dentro do escopo de tenant/projeto informado.</summary>
public sealed class CreateJobUseCase(IJobStore store)
{
    private readonly IJobStore _store = store;

    /// <summary>Persiste o Job e retorna sua identidade.</summary>
    public Task<JobId> ExecuteAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _store.CreateAsync(command, cancellationToken);
    }
}
