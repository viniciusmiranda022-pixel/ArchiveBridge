using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Reserva de uma versão de descoberta (fase 2 do protocolo recuperável): a versão foi atribuída sob
/// lock e os metadados gravados como <see cref="EvDiscoveryStatus.Pending"/>, mas a evidência ainda não
/// foi publicada nem promovida a utilizável. Carrega o caminho lógico esperado e os hashes que a
/// finalização confere contra a evidência publicada.
/// </summary>
public sealed record EvDiscoveryReservation(
    EvEnvironmentId Environment,
    int DiscoveryVersion,
    DiscoveryRunId RunId,
    string EvidenceLogicalPath,
    Sha256Hash ConfigurationHash,
    Sha256Hash EvidenceHash,
    long SizeBytes);

/// <summary>Registro persistido (metadados) de uma versão de descoberta — nunca a evidência bruta.</summary>
public sealed record EvDiscoveryRecord(
    EvEnvironmentId Environment,
    int DiscoveryVersion,
    DiscoveryRunId RunId,
    EvDiscoveryStatus Status,
    EvDiscoveryResultCode ResultCode,
    EvAdapterId? SelectedAdapter,
    int? AdapterVersion,
    string ObservedVersion,
    Sha256Hash ConfigurationHash,
    Sha256Hash EvidenceHash,
    string EvidenceLogicalPath);

/// <summary>
/// Porta de persistência dos METADADOS de descoberta (nunca a evidência bruta): versão, status, hashes,
/// adapter selecionado, caminho lógico. Mesmo protocolo em DUAS transações curtas do Slice 2 (sem I/O de
/// filesystem sob transação): <see cref="ReserveAsync"/> (tx1) → publicação FORA do SQL →
/// <see cref="FinalizeAsync"/> (tx2). Uma nova descoberta não sobrescreve a anterior; a anterior só é
/// marcada <see cref="EvDiscoveryStatus.Superseded"/> após a nova estar completa e validada. A
/// reconciliação de uma queda entre as fases usa os hashes (config + evidência).
/// </summary>
public interface IEvDiscoveryStore
{
    /// <summary>Maior versão de descoberta já registrada para o ambiente (0 se nenhuma).</summary>
    Task<int> GetMaxVersionAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Versão utilizável corrente da descoberta do ambiente (<see langword="null"/> se nenhuma).</summary>
    Task<EvDiscoveryRecord?> GetUsableAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserva PENDENTE dos mesmos hashes (config + evidência) — <see langword="null"/> se nenhuma.
    /// Recupera, após uma queda entre reserva e finalização, a versão já reservada (mesma evidência
    /// semântica), em vez de criar um novo número de versão.
    /// </summary>
    Task<EvDiscoveryReservation?> GetPendingByHashesAsync(
        TenantScope scope, EvEnvironmentId environmentId, Sha256Hash configurationHash, Sha256Hash evidenceHash, CancellationToken cancellationToken);

    /// <summary>
    /// Transação 1 (curta, SEM I/O de filesystem): valida o cercamento, calcula a próxima versão sob lock
    /// e insere os metadados como <see cref="EvDiscoveryStatus.Pending"/> (com hashes, caminho lógico
    /// esperado e tamanho). Não substitui a versão utilizável anterior. Retorna a reserva.
    /// </summary>
    Task<EvDiscoveryReservation> ReserveAsync(
        TenantScope scope,
        EvEnvironmentId environmentId,
        EvDiscoveryRunResult result,
        long evidenceSizeBytes,
        CorrelationId correlation,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transação 2 (curta): valida a evidência publicada FORA da transação
    /// (<paramref name="validatePublishedEvidenceAsync"/>, ANTES de abrir a transação/lock), revalida o
    /// cercamento, confere que a reserva pendente ainda corresponde (versão + hashes) e então promove
    /// <see cref="EvDiscoveryStatus.Pending"/> → estado terminal, marcando a utilizável anterior como
    /// Superseded somente agora. Idempotente. Inconsistência falha fechada, sem commit.
    /// </summary>
    Task<EvDiscoveryRecord> FinalizeAsync(
        TenantScope scope,
        EvDiscoveryReservation reservation,
        JobFence? fence,
        Func<CancellationToken, Task> validatePublishedEvidenceAsync,
        CancellationToken cancellationToken);
}
