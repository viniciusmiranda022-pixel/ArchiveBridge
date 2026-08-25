using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Reserva de uma versão do mapping CSV do Purview (fase 2 do protocolo recuperável): o número de versão
/// foi atribuído sob lock e a evidência gravada como <see cref="MappingVersionStatus.PendingArtifact"/>,
/// mas o artefato ainda não foi publicado nem promovido a utilizável. Carrega o caminho lógico esperado e
/// a impressão digital/hash que a finalização confere contra o artefato publicado.
/// </summary>
public sealed record PurviewMappingReservation(
    WaveId Wave,
    MappingVersion Version,
    string LogicalPath,
    PurviewMappingGenerationFingerprint Fingerprint,
    Sha256Hash ContentSha256,
    long SizeBytes);

/// <summary>
/// Porta de persistência das versões do mapping CSV do Purview e da sua evidência de metadados (item 12 —
/// somente version/hash/row count/created time/referência opaca; NUNCA o conteúdo das linhas nem o path
/// físico interno). Mesmo protocolo recuperável em DUAS transações curtas do módulo genérico do Slice 2
/// (item 8 — reuso do padrão comprovado, não do schema, que diverge por não fixar <c>IsArchive</c>/
/// <c>ContentCodePage</c>): <see cref="ReserveAsync"/> (sem I/O de filesystem) → o chamador publica o
/// artefato FORA do SQL → <see cref="FinalizeAsync"/>. Uma queda entre as fases é reconciliável por
/// <see cref="GetPendingByFingerprintAsync"/>. Uma nova geração nunca sobrescreve.
/// </summary>
public interface IPurviewMappingCsvStore
{
    /// <summary>Maior <c>mapping_version</c> já gerada para a onda (0 se nenhuma), para calcular N+1.</summary>
    Task<int> GetMaxVersionAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>Versão utilizável corrente da onda (<see langword="null"/> se nenhuma).</summary>
    Task<PurviewMappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken);

    /// <summary>
    /// Qualquer versão específica desta onda (utilizável, substituída OU pendente), por número opaco —
    /// nunca apenas a corrente utilizável. Suporta o download por referência opaca (item 13): a evidência
    /// de versões antigas é preservada, nunca apagada, e continua acessível por quem tem escopo autorizado
    /// mesmo depois de substituída. <see langword="null"/> se a versão não existir/for de outro escopo.
    /// </summary>
    Task<PurviewMappingCsvVersion?> GetByVersionAsync(TenantScope scope, WaveId waveId, MappingVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// Reserva PENDENTE (não utilizável) da mesma impressão digital (<see langword="null"/> se nenhuma) —
    /// recupera, após uma queda entre a reserva e a finalização, a versão já reservada.
    /// </summary>
    Task<PurviewMappingReservation?> GetPendingByFingerprintAsync(
        TenantScope scope, WaveId waveId, PurviewMappingGenerationFingerprint fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Transação 1 (curta, SEM I/O de filesystem): calcula o próximo número de versão (N+1) sob lock e
    /// insere a nova versão como <see cref="MappingVersionStatus.PendingArtifact"/>. Não substitui a
    /// versão utilizável anterior — ela permanece utilizável até a finalização.
    /// </summary>
    Task<PurviewMappingReservation> ReserveAsync(
        TenantScope scope,
        PurviewMappingGenerationResult result,
        long expectedSizeBytes,
        JobFence? fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transação 2 (curta): valida o artefato publicado FORA da transação
    /// (<paramref name="validatePublishedArtifactAsync"/>), confere que a reserva pendente ainda
    /// corresponde (versão + fingerprint + hash) e promove PendingArtifact → Usable, marcando a versão
    /// utilizável anterior como Superseded somente agora. Idempotente. Inconsistência falha fechada.
    /// </summary>
    Task<PurviewMappingCsvVersion> FinalizeAsync(
        TenantScope scope,
        PurviewMappingReservation reservation,
        JobFence? fence,
        Func<CancellationToken, Task> validatePublishedArtifactAsync,
        CancellationToken cancellationToken);
}
