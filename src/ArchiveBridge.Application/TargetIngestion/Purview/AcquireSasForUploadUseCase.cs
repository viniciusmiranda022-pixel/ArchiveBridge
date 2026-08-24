using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>
/// Solicitação de aquisição do SAS custodiado para upload. <see cref="Requester"/> é a identidade de
/// workload AFIRMADA pelo composition root a partir do transporte autenticado (mesmo padrão de
/// <see cref="WorkloadIdentity"/> — a vinculação real a uma identidade Windows/certificado é tarefa do
/// composition root do futuro <c>ArchiveBridge.Workers.Upload</c>, fora do escopo deste Passo).
/// </summary>
public sealed record AcquireSasForUploadRequest(TenantScope Scope, WaveId Wave, WorkloadIdentity Requester, CorrelationId Correlation);

/// <summary>
/// Prepara (sem executar nenhum processo externo — STOP-THE-LINE deste Passo) a operação
/// <c>AcquireForUpload</c> exigida pelo work order AB-I5-004 item 11: uso único, autorizado por
/// tenant/projeto/wave E por identidade de workload, nunca reaquire um handle expirado/destruído/já
/// consumido. Controle/API não tem NENHUM outro caminho para ler o segredo em texto claro — este é o
/// ÚNICO caso de uso de toda a Application que chama <see cref="ISecretStore.AcquireAsync"/>
/// (reforçado por <c>PurviewSasSecretBoundaryTests</c> na Architecture.Tests: nenhum outro host além do
/// futuro upload worker pode sequer referenciar este tipo).
/// </summary>
public sealed class AcquireSasForUploadUseCase(IPurviewSasUploadHandleStore handles, ISecretStore secrets, IClock clock)
{
    private const string DenialMessage =
        "Aquisição recusada (fail-closed): nenhum SAS disponível para adquirir neste escopo/identidade.";

    private readonly IPurviewSasUploadHandleStore _handles = handles;
    private readonly ISecretStore _secrets = secrets;
    private readonly IClock _clock = clock;

    /// <summary>
    /// Adquire o SAS custodiado — uma única vez por geração (policy de uso único deste Passo). Onda sem
    /// handle, requester não autorizado, handle fora de <see cref="SasHandleState.Available"/> (Stored,
    /// já Consumed, Expired ou Destroyed) e expiry ultrapassado produzem TODOS o MESMO tipo de exceção,
    /// sem vazar qual causa se aplica.
    /// </summary>
    /// <exception cref="PurviewSasAcquisitionDeniedException">Aquisição recusada fail-closed (ver acima).</exception>
    public async Task<RedactedSecret> ExecuteAsync(AcquireSasForUploadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Requester.Value, WorkloadIdentities.UploadWorker.Value, StringComparison.Ordinal))
        {
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        var handle = await _handles.GetCanonicalAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        var now = _clock.UtcNow;
        if (handle.State == SasHandleState.Available && handle.ExpiresAtUtc <= now)
        {
            // Expiração avaliada de forma preguiçosa (lazy) no primeiro acesso após o vencimento —
            // marca o estado explicitamente (item 9: transição auditável) e recusa a aquisição.
            await _handles.SaveTransitionAsync(handle.MarkExpired(now), cancellationToken).ConfigureAwait(false);
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        if (handle.State != SasHandleState.Available)
        {
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        // Uso único: reivindica a transição Available -> Consumed por concorrência otimista (row_version)
        // ANTES de revelar o segredo. Isto é o que torna o uso único seguro sob corrida: se duas
        // aquisições concorrentes lessem o mesmo handle Available e SÓ DEPOIS disputassem a transição, as
        // DUAS teriam já recebido o texto claro do secret store antes de a corrida ser resolvida — a
        // ordem abaixo garante que NENHUM segredo é revelado a um chamador que perde a corrida.
        PurviewSasUploadHandle consumed;
        try
        {
            consumed = await _handles.SaveTransitionAsync(handle.MarkConsumed(now), cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        _ = consumed;
        try
        {
            return await _secrets
                .AcquireAsync(request.Scope, handle.SecretStoreReference, request.Requester, request.Correlation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SecretStoreAccessDeniedException)
        {
            // Uniformiza a superfície de exceção da Application (mesmo tipo/mensagem genérica de TODAS as
            // causas de negação) mesmo quando o adapter concreto recusa por sua própria revalidação de
            // defesa em profundidade.
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }
    }
}
