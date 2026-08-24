using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;

/// <summary>Resultado de um enfileiramento idempotente: o pedido/Job (existente ou recém-criado) e se foi réplay.</summary>
public sealed record PurviewUploadRequestEnqueueResult(JobId JobId, PurviewUploadRequestId RequestId, bool Created, bool Replayed);

/// <summary>
/// Persistência do pedido lógico DURÁVEL de upload Purview de uma wave (AB-I5-009 item 8). O enfileiramento
/// é SEMPRE idempotente por (tenant, projeto, wave) — para sempre um único pedido/Job por wave, nunca dois
/// (item 14/8: "nunca duplicar um upload lógico silenciosamente"). O CLAIM/execução do Job reutiliza
/// diretamente <c>IJobStore</c>/<c>IJobLeaseManager</c> (ADR-0003) — esta porta cobre apenas a criação
/// idempotente e a projeção (pedido ↔ Job).
/// </summary>
public interface IPurviewUploadRequestStore
{
    /// <summary>
    /// Cria o pedido + Job (workload <see cref="ArchiveBridge.Domain.IdentityAndAccess.Workload.Upload"/>)
    /// atomicamente SE ainda não existir um pedido canônico para a wave; caso contrário devolve o
    /// existente (réplay). Um índice único filtrado no SQL é o backstop de concorrência.
    /// </summary>
    Task<PurviewUploadRequestEnqueueResult> EnqueueIdempotentAsync(
        TenantScope scope, WaveId wave, CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>Pedido canônico da wave, se algum upload já foi solicitado neste escopo; <see langword="null"/> caso contrário.</summary>
    Task<PurviewUploadRequest?> FindCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken);

    /// <summary>Pedido vinculado a um Job já reivindicado (usado pelo command processor após o claim); <see langword="null"/> se inexistente/fora do escopo.</summary>
    Task<PurviewUploadRequest?> GetByJobAsync(TenantScope scope, JobId job, CancellationToken cancellationToken);
}
