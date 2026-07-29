using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;

namespace ArchiveBridge.Application.Jobs;

/// <summary>
/// Caso de uso mínimo (scaffolding): reivindica o próximo job de um workload pela porta da fila.
/// Existe para provar a regra de dependência — a Application depende apenas de Domain e Contracts,
/// e orquestra através de <see cref="IJobLeaseSource"/> sem conhecer nenhuma implementação concreta.
/// Não executa migração real.
/// </summary>
public sealed class ClaimNextJobUseCase(IJobLeaseSource source)
{
    private readonly IJobLeaseSource _source = source;

    /// <summary>Reivindica o próximo job elegível do workload; <see langword="null"/> quando não há trabalho.</summary>
    public Task<JobLease?> ExecuteAsync(Workload workload, CancellationToken cancellationToken) =>
        _source.TryClaimNextAsync(workload, cancellationToken);
}
