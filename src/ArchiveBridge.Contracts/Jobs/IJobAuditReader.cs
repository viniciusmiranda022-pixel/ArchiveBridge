using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Contracts.Jobs;

/// <summary>
/// Porta de leitura da trilha de auditoria de transições. As <b>escritas</b> de auditoria são
/// intrínsecas às operações da fila e ocorrem na mesma transação da mudança de estado (atômicas,
/// nunca perdidas) — por isso a auditoria expõe aqui apenas a consulta, usada por observabilidade
/// e testes.
/// </summary>
public interface IJobAuditReader
{
    /// <summary>Lê as transições de um Job dentro do escopo, em ordem cronológica.</summary>
    Task<IReadOnlyList<JobTransitionRecord>> GetTransitionsAsync(
        TenantScope scope,
        JobId jobId,
        CancellationToken cancellationToken);
}
