using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>
/// Reserva de uma versão de descoberta (fase 2 do protocolo recuperável): a versão foi atribuída sob lock
/// e os metadados gravados como <see cref="EvDiscoveryStatus.Pending"/>, identificados pelos TRÊS hashes —
/// configuração completa, evidência semântica completa e SHA-256 do conteúdo do <c>evidence.json</c>.
/// <see cref="Reconciled"/> indica que a reserva foi RECUPERADA (já existia uma pendente equivalente), não
/// criada agora.
/// </summary>
public sealed record EvDiscoveryReservation(
    EvEnvironmentId Environment,
    int DiscoveryVersion,
    DiscoveryRunId RunId,
    string EvidenceLogicalPath,
    Sha256Hash ConfigurationHash,
    Sha256Hash SemanticEvidenceHash,
    Sha256Hash ContentSha256,
    long SizeBytes,
    bool Reconciled);

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
    Sha256Hash SemanticEvidenceHash,
    Sha256Hash ContentSha256,
    string EvidenceLogicalPath);

/// <summary>
/// Porta de persistência dos METADADOS de descoberta (nunca a evidência bruta): versão, status, os três
/// hashes (configuração/evidência semântica/conteúdo), adapter selecionado, caminho lógico. Mesmo protocolo
/// em DUAS transações curtas do Slice 2 (sem I/O de filesystem sob transação): <see cref="ReserveAsync"/>
/// (tx1) → publicação FORA do SQL → <see cref="FinalizeAsync"/> (tx2). A reserva é ATÔMICA: verifica a
/// pendente equivalente e a insere na MESMA transação sob lock (sem duas versões Pending para a mesma
/// evidência). Uma nova descoberta não sobrescreve a anterior; a anterior só vira
/// <see cref="EvDiscoveryStatus.Superseded"/> após a nova estar completa e validada.
/// </summary>
public interface IEvDiscoveryStore
{
    /// <summary>Maior versão de descoberta já registrada para o ambiente (0 se nenhuma).</summary>
    Task<int> GetMaxVersionAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>Versão utilizável corrente da descoberta do ambiente (<see langword="null"/> se nenhuma).</summary>
    Task<EvDiscoveryRecord?> GetUsableAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Transação 1 (curta, SEM I/O de filesystem): valida o cercamento e, ATOMICAMENTE sob lock, verifica
    /// se já existe uma reserva PENDENTE dos mesmos três hashes — se existir devolve-a
    /// (<see cref="EvDiscoveryReservation.Reconciled"/> = true); senão calcula N+1 e insere a versão como
    /// <see cref="EvDiscoveryStatus.Pending"/>. Não substitui a versão utilizável anterior. Um índice único
    /// filtrado impede duas Pending para a mesma evidência.
    /// </summary>
    Task<EvDiscoveryReservation> ReserveAsync(
        TenantScope scope,
        EvEnvironmentId environmentId,
        EvDiscoveryRunResult result,
        Sha256Hash configurationHash,
        Sha256Hash semanticEvidenceHash,
        Sha256Hash contentSha256,
        long evidenceSizeBytes,
        CorrelationId correlation,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transação 2 (curta): lê a evidência publicada FORA da transação
    /// (<paramref name="readPublishedEvidenceAsync"/>, ANTES de abrir a transação/lock) e CONFERE que o
    /// artefato publicado corresponde à reserva — caminho lógico, tamanho em bytes e SHA-256 do conteúdo
    /// devem bater com <see cref="EvDiscoveryReservation.EvidenceLogicalPath"/>,
    /// <see cref="EvDiscoveryReservation.SizeBytes"/> e <see cref="EvDiscoveryReservation.ContentSha256"/>.
    /// Em seguida revalida o cercamento, confere que a reserva pendente ainda corresponde aos TRÊS hashes
    /// AUTORITATIVOS (configuração/evidência semântica/conteúdo) e promove
    /// <see cref="EvDiscoveryStatus.Pending"/> → estado terminal, marcando a utilizável anterior como
    /// Superseded somente agora. Idempotente; qualquer divergência falha fechada, sem commit e sem
    /// substituir a versão anterior.
    /// </summary>
    Task<EvDiscoveryRecord> FinalizeAsync(
        TenantScope scope,
        EvDiscoveryReservation reservation,
        JobFence? fence,
        Func<CancellationToken, Task<EvDiscoveryEvidenceReference>> readPublishedEvidenceAsync,
        CancellationToken cancellationToken);
}
