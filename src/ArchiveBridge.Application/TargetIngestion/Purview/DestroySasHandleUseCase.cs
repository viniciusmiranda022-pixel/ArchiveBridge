using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>Solicitação de destruição explícita do material local do SAS custodiado (work order AB-I5-004 item 12).</summary>
public sealed record DestroySasHandleRequest(TenantScope Scope, WaveId Wave, CorrelationId Correlation);

/// <summary>
/// Destrói localmente o material secreto após consumo final, expiração ou cancelamento explícito da wave
/// (item 12) — registra apenas evidência de lifecycle, NUNCA representa a destruição local como
/// revogação remota do SAS no serviço Microsoft (item 12/STOP-THE-LINE). Idempotente: destruir um handle
/// já <see cref="SasHandleState.Destroyed"/> é um no-op bem-sucedido (mesmo comportamento de
/// <see cref="PurviewSasUploadHandle.Destroy"/>).
/// </summary>
public sealed class DestroySasHandleUseCase(IPurviewSasUploadHandleStore handles, ISecretStore secrets, IClock clock)
{
    private const string HandleNotFoundMessage =
        "Destruição recusada (fail-closed): nenhum handle de SAS encontrado neste escopo.";

    private readonly IPurviewSasUploadHandleStore _handles = handles;
    private readonly ISecretStore _secrets = secrets;
    private readonly IClock _clock = clock;

    /// <exception cref="PurviewSasAcquisitionDeniedException">Nenhum handle canônico existe neste escopo/wave.</exception>
    public async Task<PurviewSasUploadHandle> ExecuteAsync(DestroySasHandleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var handle = await _handles.GetCanonicalAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewSasAcquisitionDeniedException(HandleNotFoundMessage);

        if (handle.State == SasHandleState.Destroyed)
        {
            return handle;
        }

        await _secrets.DestroyAsync(request.Scope, handle.SecretStoreReference, request.Correlation, cancellationToken)
            .ConfigureAwait(false);

        return await _handles.SaveTransitionAsync(handle.Destroy(_clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }
}
