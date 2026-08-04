using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Mapping;

/// <summary>
/// Reserva de uma versão de mapping (fase 2 do protocolo recuperável): o número de versão foi
/// atribuído sob lock e a evidência gravada como <see cref="MappingVersionStatus.PendingArtifact"/>,
/// mas o artefato ainda não foi publicado nem promovido a utilizável. Carrega o caminho lógico
/// esperado e a impressão digital/hash que a finalização confere contra o artefato publicado.
/// </summary>
public sealed record MappingReservation(
    WaveId Wave,
    MappingVersion Version,
    string LogicalPath,
    MappingGenerationFingerprint Fingerprint,
    Sha256Hash ContentSha256,
    long SizeBytes);

/// <summary>
/// Porta de persistência das versões de mapping e da sua evidência (linhas + sha256). Uma nova
/// geração nunca sobrescreve: reserva a versão N+1 e, após publicar o artefato, promove-a a utilizável
/// marcando a anterior como substituída (Superseded). Não persiste segredo nem conteúdo de e-mail/PST
/// (apenas metadados do CSV e o hash). O conteúdo bruto do CSV é entregue ao chamador como artefato.
///
/// <para>
/// Protocolo em DUAS transações curtas, para NUNCA manter a transação SQL / os locks abertos durante o
/// I/O de filesystem (crítico em SMB/NAS): <see cref="ReserveAsync"/> (transação 1, sem I/O de
/// filesystem) → o chamador publica o artefato FORA do SQL → <see cref="FinalizeAsync"/> (transação 2).
/// Uma queda entre as fases é reconciliável por <see cref="GetPendingByFingerprintAsync"/>: a mesma
/// geração recupera a reserva pendente (sem criar versão indevida) e a finaliza.
/// </para>
/// </summary>
public interface IMappingStore
{
    /// <summary>Maior <c>mapping_version</c> já gerada para a onda (0 se nenhuma), para calcular N+1.</summary>
    Task<int> GetMaxVersionAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>
    /// Versão utilizável corrente da onda (<see langword="null"/> se nenhuma). Usada para tornar a
    /// geração idempotente em retry: se já existe uma versão utilizável para a mesma seleção, não se
    /// gera outra.
    /// </summary>
    Task<MappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserva PENDENTE (não utilizável) da mesma impressão digital (<see langword="null"/> se nenhuma).
    /// Recupera, após uma queda entre a reserva e a finalização, a versão já reservada — a mesma geração
    /// a republica e finaliza em vez de reservar um novo número de versão.
    /// </summary>
    Task<MappingReservation?> GetPendingByFingerprintAsync(
        TenantScope scope, WaveId waveId, MappingGenerationFingerprint fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Transação 1 (curta, SEM I/O de filesystem): valida o cercamento do Job (<paramref name="fence"/>),
    /// calcula o próximo número de versão (N+1) sob lock (sequência atômica, sem TOCTOU) e insere a nova
    /// versão como <see cref="MappingVersionStatus.PendingArtifact"/> (com fingerprint, o caminho lógico
    /// esperado e o tamanho) junto das linhas de evidência. Não substitui a versão utilizável anterior —
    /// ela permanece utilizável até a finalização. Retorna a reserva (versão + caminho esperado).
    /// </summary>
    Task<MappingReservation> ReserveAsync(
        TenantScope scope,
        MappingGenerationResult result,
        long expectedSizeBytes,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transação 2 (curta): valida o artefato publicado FORA da transação
    /// (<paramref name="validatePublishedArtifactAsync"/>, executado ANTES de abrir a transação/lock),
    /// revalida o cercamento, confere que a reserva pendente ainda corresponde
    /// (versão + fingerprint + hash) e então promove <see cref="MappingVersionStatus.PendingArtifact"/> →
    /// <see cref="MappingVersionStatus.Usable"/>, marcando a versão utilizável anterior como Superseded
    /// somente agora. Idempotente: uma reserva já finalizada (já utilizável/substituída) é aceita sem
    /// novo efeito. Inconsistência (artefato incompleto/divergente, reserva ausente) falha fechada, sem
    /// commit. Retorna a evidência utilizável final.
    /// </summary>
    Task<MappingCsvVersion> FinalizeAsync(
        TenantScope scope,
        MappingReservation reservation,
        JobFence? fence,
        Func<CancellationToken, Task> validatePublishedArtifactAsync,
        CancellationToken cancellationToken);
}
