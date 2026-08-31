using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview;

/// <summary>Resultado de <see cref="IMailboxPrecheckStore.AppendAsync"/> — <see cref="Created"/> falso indica réplay idempotente.</summary>
public sealed record MailboxPrecheckAppendResult(MailboxPrecheckSnapshot Snapshot, bool Created);

/// <summary>
/// Store append-only de <see cref="MailboxPrecheckSnapshot"/>, escopado a tenant/projeto/archive. Nenhuma
/// linha é atualizada ou removida — <see cref="GetLatestAsync"/> sempre lê o snapshot vigente (mais recente
/// por <see cref="MailboxPrecheckSnapshot.Version"/>) diretamente do histórico completo.
/// </summary>
public interface IMailboxPrecheckStore
{
    /// <summary>Devolve o snapshot vigente (mais recente) dentro do escopo; <see langword="null"/> se nenhum existir.</summary>
    Task<MailboxPrecheckSnapshot?> GetLatestAsync(TenantScope scope, TargetArchiveId mailbox, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve o snapshot mais recente (por <see cref="MailboxPrecheckSnapshot.RecordedAtUtc"/>) dentre TODOS
    /// os mailboxes/archives já prechecados neste tenant/projeto — <see langword="null"/> se nenhum precheck
    /// existir. Usado pelo Production Readiness Review (AB-I8-002), que é escopado a tenant/projeto, não a um
    /// mailbox específico; nunca filtra por mailbox.
    /// </summary>
    Task<MailboxPrecheckSnapshot?> GetLatestAcrossMailboxesAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Persiste uma nova versão (append). Se a versão candidata já foi ocupada por outra submissão
    /// concorrente com o MESMO conteúdo lógico, converge (<see cref="MailboxPrecheckAppendResult.Created"/>
    /// = <see langword="false"/>); com conteúdo diferente, lança <see cref="ArchiveBridge.Domain.Common.ConcurrencyException"/>.
    /// </summary>
    Task<MailboxPrecheckAppendResult> AppendAsync(MailboxPrecheckSnapshot snapshot, CancellationToken cancellationToken);
}
