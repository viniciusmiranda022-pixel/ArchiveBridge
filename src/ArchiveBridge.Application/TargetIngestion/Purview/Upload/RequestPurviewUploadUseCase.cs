using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Upload;

/// <summary>Solicita, de forma idempotente, o upload Purview de uma wave já aprovada.</summary>
public sealed record RequestPurviewUploadRequest(TenantScope Scope, WaveId Wave, CorrelationId Correlation);

/// <summary>
/// Enfileira o pedido lógico DURÁVEL de upload de uma wave (AB-I5-009 item 8) — sempre idempotente por
/// (tenant, projeto, wave): uma segunda solicitação para a MESMA wave devolve o pedido/Job já existentes
/// (réplay), nunca cria um segundo. A wave é resolvida server-side via <see cref="IWaveStore"/> (nunca
/// aceita path/host/SAS do caller — item 2) e só é elegível quando sua seleção já está CONGELADA
/// (<see cref="WaveStatus.Approved"/>/<see cref="WaveStatus.Frozen"/>) — uma onda ainda mutável
/// (Draft/Validating/Blocked/ReadyForApproval) nunca é aceita: o conjunto de PSTs a transportar tem de ser
/// o mesmo que foi (ou será) aprovado, nunca um estado intermediário.
/// </summary>
public sealed class RequestPurviewUploadUseCase(IWaveStore waves, IPurviewUploadRequestStore requests)
{
    private const string NotEligibleMessage =
        "Pedido de upload recusado (fail-closed): onda inexistente, fora do escopo autorizado, ou seleção ainda não congelada (Approved/Frozen).";

    private readonly IWaveStore _waves = waves;
    private readonly IPurviewUploadRequestStore _requests = requests;

    /// <summary>Cria (ou converge idempotentemente para) o pedido lógico de upload.</summary>
    /// <exception cref="PurviewUploadWaveNotEligibleException">Onda inexistente, fora de escopo, ou seleção ainda mutável.</exception>
    public async Task<PurviewUploadRequestEnqueueResult> ExecuteAsync(
        RequestPurviewUploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wave = await _waves.GetAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false);
        if (wave is null || wave.Status is not (WaveStatus.Approved or WaveStatus.Frozen))
        {
            throw new PurviewUploadWaveNotEligibleException(NotEligibleMessage);
        }

        return await _requests
            .EnqueueIdempotentAsync(request.Scope, request.Wave, request.Correlation, cancellationToken)
            .ConfigureAwait(false);
    }
}
