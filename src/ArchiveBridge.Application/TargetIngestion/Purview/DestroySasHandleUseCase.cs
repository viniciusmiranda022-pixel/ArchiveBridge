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
/// já <see cref="SasHandleState.Destroyed"/> é um no-op bem-sucedido em relação ao RESULTADO (mesmo
/// comportamento de <see cref="PurviewSasUploadHandle.Destroy"/>).
/// <para>
/// Ordem crash-consistente (AB-I5-006 item 3): o metadado é transicionado para
/// <see cref="SasHandleState.Destroyed"/> PRIMEIRO — a partir daí o handle fica inacessível a
/// <c>AcquireSasForUploadUseCase</c> (que só reivindica a partir de Available/Claimed) — e SOMENTE DEPOIS o
/// material é removido do secret store. Se o processo cair/cancelar ENTRE as duas etapas, o metadado JÁ
/// mostra Destroyed (nunca aparenta disponível apontando para material já apagado) e uma nova chamada a
/// este caso de uso RETOMA exatamente a partir da destruição do material (idempotente: <c>ISecretStore.
/// DestroyAsync</c> já é idempotente por si só, e agora é sempre reexecutado até confirmar sucesso, mesmo
/// quando o metadado já estava Destroyed de uma tentativa anterior).
/// </para>
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

        // Metadado PRIMEIRO (fencing/inacessibilidade durável) — só transiciona quando ainda não estava
        // Destroyed; uma tentativa anterior que já concluiu esta etapa mas caiu antes da próxima (material)
        // não repete a transição — apenas RETOMA a partir da destruição do material abaixo.
        var destroyed = handle.State == SasHandleState.Destroyed
            ? handle
            : await _handles.SaveTransitionAsync(handle.Destroy(_clock.UtcNow), cancellationToken).ConfigureAwait(false);

        // Material DEPOIS — sempre reexecutado (idempotente por si só em ISecretStore.DestroyAsync), mesmo
        // quando o metadado já estava Destroyed: garante que uma tentativa anterior que caiu ENTRE as duas
        // etapas eventualmente converge (retry desta chamada) em vez de deixar o material para sempre.
        await _secrets.DestroyAsync(request.Scope, handle.SecretStoreReference, request.Correlation, cancellationToken)
            .ConfigureAwait(false);

        return destroyed;
    }
}
