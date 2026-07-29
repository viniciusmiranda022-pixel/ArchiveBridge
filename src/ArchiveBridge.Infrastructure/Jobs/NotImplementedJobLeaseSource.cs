using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;

namespace ArchiveBridge.Infrastructure.Jobs;

/// <summary>
/// SCAFFOLDING NÃO FUNCIONAL — placeholder deliberado.
/// Existe apenas para provar a regra de portas/adapters: a Infrastructure implementa
/// <see cref="IJobLeaseSource"/> (definida em Contracts) sem que o domínio ou a aplicação
/// conheçam o mecanismo. NÃO há fila durável em SQL Server (ADR-0003) por trás desta classe —
/// a implementação real entra por slice posterior. Não representa capacidade funcional de migração.
/// </summary>
public sealed class NotImplementedJobLeaseSource : IJobLeaseSource
{
    /// <inheritdoc />
    public Task<JobLease?> TryClaimNextAsync(Workload workload, CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            "Scaffolding: a fila durável em SQL Server (ADR-0003) ainda não foi implementada. " +
            "Nenhuma capacidade funcional de migração está disponível.");
}
