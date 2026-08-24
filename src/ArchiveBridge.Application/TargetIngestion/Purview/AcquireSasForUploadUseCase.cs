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
/// <c>AcquireForUpload</c> exigida pelo work order AB-I5-004 item 11, com a semântica de claim/lease/
/// fencing exigida por AB-I5-006 item 2: uso único, autorizado por tenant/projeto/wave E por identidade de
/// workload, nunca reaquire um handle expirado/destruído/já consumido. Controle/API não tem NENHUM outro
/// caminho para ler o segredo em texto claro — este é o ÚNICO caso de uso de toda a Application que chama
/// <see cref="ISecretStore.AcquireAsync"/> (reforçado por <c>PurviewSasSecretBoundaryTests</c> na
/// Architecture.Tests: nenhum outro host além do futuro upload worker pode sequer referenciar este tipo).
/// <para>
/// Ciclo por chamada: (1) reivindica <see cref="SasHandleState.Available"/> -&gt;
/// <see cref="SasHandleState.Claimed"/> sob concorrência otimista (<c>row_version</c>) — a perdedora de uma
/// corrida de PRIMEIRA reivindicação NUNCA chega a chamar <see cref="ISecretStore.AcquireAsync"/>; um
/// claim já ativo e ainda dentro do lease é recusado imediatamente (outro adquirente em voo); um claim com
/// lease EXPIRADO é recuperável via <see cref="PurviewSasUploadHandle.Reclaim"/> (fencing por época — o
/// titular anterior nunca mais finaliza com a época antiga). (2) SOMENTE depois disso o segredo é lido.
/// (3) a transição final para <see cref="SasHandleState.Consumed"/> só ocorre DEPOIS da leitura bem-sucedida
/// (nunca antes — ao contrário do desenho anterior a AB-I5-005/006): uma falha do secret store, cancelamento
/// ou queda de processo ENTRE o claim e a leitura nunca queima a geração — o lease simplesmente expira e um
/// novo adquirente (ou o mesmo, em retry) recupera via <see cref="PurviewSasUploadHandle.Reclaim"/>.
/// </para>
/// </summary>
public sealed class AcquireSasForUploadUseCase
{
    /// <summary>
    /// Duração default do lease de claim (AB-I5-006 item 2) — política PRÓPRIA do produto (não documentada
    /// pela Microsoft), suficiente para o boundary autorizado ler o segredo do secret store sem uma segunda
    /// rede de I/O externa (vedada pelo STOP-THE-LINE deste Passo). Nunca excede a validade restante do
    /// próprio SAS (<see cref="PurviewSasUploadHandle.ExpiresAtUtc"/>) — ver <see cref="ExecuteAsync"/>.
    /// </summary>
    public static readonly TimeSpan DefaultClaimLeaseDuration = TimeSpan.FromMinutes(5);

    private const string DenialMessage =
        "Aquisição recusada (fail-closed): nenhum SAS disponível para adquirir neste escopo/identidade.";

    private readonly IPurviewSasUploadHandleStore _handles;
    private readonly ISecretStore _secrets;
    private readonly IClock _clock;
    private readonly TimeSpan _claimLeaseDuration;

    /// <summary>Cria o caso de uso com o lease de claim default (<see cref="DefaultClaimLeaseDuration"/>).</summary>
    public AcquireSasForUploadUseCase(IPurviewSasUploadHandleStore handles, ISecretStore secrets, IClock clock)
        : this(handles, secrets, clock, DefaultClaimLeaseDuration)
    {
    }

    /// <summary>Cria o caso de uso com um lease de claim explícito (permite policy própria do composition root).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="claimLeaseDuration"/> não é estritamente positivo.</exception>
    public AcquireSasForUploadUseCase(
        IPurviewSasUploadHandleStore handles, ISecretStore secrets, IClock clock, TimeSpan claimLeaseDuration)
    {
        if (claimLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(claimLeaseDuration), claimLeaseDuration, "O lease de claim deve ser estritamente positivo.");
        }

        _handles = handles;
        _secrets = secrets;
        _clock = clock;
        _claimLeaseDuration = claimLeaseDuration;
    }

    /// <summary>
    /// Adquire o SAS custodiado — uma única vez por geração (uso único). Onda sem handle, requester não
    /// autorizado, handle fora de Available/Claimed-recuperável (Stored, Claimed com lease ainda ativo de
    /// outro adquirente, já Consumed, Expired ou Destroyed) e expiry ultrapassado produzem TODOS o MESMO
    /// tipo de exceção, sem vazar qual causa se aplica.
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

        // Expiração do próprio SAS (independente de qualquer claim) — avaliada preguiçosamente no primeiro
        // acesso após o vencimento, igual ao comportamento já aceito antes deste item (marca o estado
        // explicitamente — item 9: transição auditável — e recusa a aquisição).
        if (handle.State is SasHandleState.Available or SasHandleState.Claimed && handle.ExpiresAtUtc <= now)
        {
            await _handles.SaveTransitionAsync(handle.MarkExpired(now), cancellationToken).ConfigureAwait(false);
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        // O lease de claim nunca ultrapassa a validade restante do próprio SAS — checado acima que
        // handle.ExpiresAtUtc > now, então o resultado abaixo é sempre estritamente futuro.
        var leaseExpiresAtUtc = Min(now + _claimLeaseDuration, handle.ExpiresAtUtc);

        PurviewSasUploadHandle claimed;
        try
        {
            claimed = handle.State switch
            {
                // Reivindicação inicial: concorrência otimista (row_version) decide qual adquirente vence —
                // a(s) perdedora(s) nunca chegam a chamar ISecretStore.AcquireAsync (ver catch abaixo).
                SasHandleState.Available =>
                    await _handles.SaveTransitionAsync(handle.Claim(request.Requester, leaseExpiresAtUtc, now), cancellationToken)
                        .ConfigureAwait(false),

                // Lease titular já expirado (adquirente anterior morreu/cancelou/nunca voltou): reivindicação
                // de recuperação sob fencing por época — o titular anterior nunca mais finaliza com a época
                // antiga, mesmo que retorne tarde demais.
                SasHandleState.Claimed when handle.ClaimExpiresAtUtc is { } leaseExpiry && leaseExpiry <= now =>
                    await _handles.SaveTransitionAsync(handle.Reclaim(request.Requester, leaseExpiresAtUtc, now), cancellationToken)
                        .ConfigureAwait(false),

                // Stored (ainda não confirmado), Claimed com lease de OUTRO adquirente ainda ativo, Consumed,
                // Expired ou Destroyed — todos recusados fail-closed, sem tocar o secret store.
                _ => throw new PurviewSasAcquisitionDeniedException(DenialMessage),
            };
        }
        catch (ConcurrencyException)
        {
            // Outro adquirente venceu a corrida de claim/reclaim entre a leitura acima e esta tentativa —
            // este requester nunca chega a ver o segredo.
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        RedactedSecret secret;
        try
        {
            secret = await _secrets
                .AcquireAsync(request.Scope, claimed.SecretStoreReference, request.Requester, request.Correlation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SecretStoreAccessDeniedException)
        {
            // Uniformiza a superfície de exceção da Application (mesmo tipo/mensagem genérica de TODAS as
            // causas de negação) mesmo quando o adapter concreto recusa por sua própria revalidação de
            // defesa em profundidade. O claim permanece ativo (nem finalizado nem liberado): expira pelo
            // lease normalmente — recuperável por reclaim, nunca queima a geração (AB-I5-006 item 2).
            throw new PurviewSasAcquisitionDeniedException(DenialMessage);
        }

        // Finaliza SOMENTE depois da leitura bem-sucedida do secret store (item 2), sob a MESMA época do
        // claim que fizemos acima (fencing): se outro adquirente já reivindicou este handle por reclaim
        // entre a aquisição do segredo e este ponto (janela mínima, sem I/O de rede entre as duas chamadas),
        // a finalização falha por row_version divergente — o segredo já lido por ESTE requester não é
        // "desfeito" (o caller decide o que fazer com uma leitura já concretizada), mas o handle nunca é
        // deixado apontando para um estado que este requester não detém mais.
        try
        {
            await _handles.SaveTransitionAsync(claimed.FinalizeClaim(request.Requester, claimed.ClaimEpoch, now), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            // Corrida residual e extremamente estreita (ver comentário acima) — best-effort: não bloqueia a
            // entrega do segredo já lida com sucesso por este requester.
        }

        return secret;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
