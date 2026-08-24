using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview;

/// <summary>
/// Persistência do metadado (opaco) de <see cref="PurviewSasUploadHandle"/> — nunca do segredo em si
/// (custódia é <see cref="ISecretStore"/>). Escopado a tenant/projeto/wave (AB-I5-004 item 2/8).
/// </summary>
public interface IPurviewSasUploadHandleStore
{
    /// <summary>
    /// Devolve o handle CANÔNICO (o mais recente, em qualquer estado) da wave; <see langword="null"/> se
    /// nenhum intake foi feito ainda neste escopo.
    /// </summary>
    Task<PurviewSasUploadHandle?> GetCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken);

    /// <summary>Devolve um handle específico por identidade, dentro do escopo; <see langword="null"/> se inexistente/fora do escopo.</summary>
    Task<PurviewSasUploadHandle?> GetByIdAsync(TenantScope scope, SasHandleId id, CancellationToken cancellationToken);

    /// <summary>
    /// Substitui ATOMICAMENTE o handle canônico da wave: se <paramref name="expectedPrevious"/> for
    /// informado, marca-o <see cref="SasHandleState.Destroyed"/> (com o <see cref="RowVersion"/> lido);
    /// em seguida insere <paramref name="candidate"/> (item 15: nova geração explícita e auditável). O
    /// índice único filtrado (tenant, projeto, wave) sobre estados "vivos" (Stored/Available/Consumed) é
    /// o backstop de concorrência: uma corrida entre dois intakes concorrentes para a MESMA wave nunca
    /// produz dois handles canônicos simultaneamente (item 16) — o perdedor recebe
    /// <see cref="ConcurrencyException"/> e deve reler o canônico atual e tentar de novo.
    /// </summary>
    /// <exception cref="ConcurrencyException">
    /// O canônico mudou desde a leitura de <paramref name="expectedPrevious"/>, ou já existe outro handle
    /// "vivo" para a wave (corrida de intake).
    /// </exception>
    Task<PurviewSasUploadHandle> ReplaceCanonicalAsync(
        TenantScope scope,
        WaveId wave,
        PurviewSasUploadHandle? expectedPrevious,
        PurviewSasUploadHandle candidate,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persiste uma transição de ciclo de vida (mesma linha, mesma <see cref="PurviewSasUploadHandle.Generation"/>)
    /// usando o <see cref="RowVersion"/> carregado em <paramref name="handle"/> como token de concorrência otimista.
    /// </summary>
    /// <exception cref="ConcurrencyException">O <see cref="RowVersion"/> divergiu (mutado concorrentemente).</exception>
    Task<PurviewSasUploadHandle> SaveTransitionAsync(PurviewSasUploadHandle handle, CancellationToken cancellationToken);
}
