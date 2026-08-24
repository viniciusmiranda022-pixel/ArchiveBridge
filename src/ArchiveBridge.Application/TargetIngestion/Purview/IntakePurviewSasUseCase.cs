using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>
/// Solicitação de intake de um SAS do Purview Network Upload — carrega SOMENTE identificadores opacos
/// (<see cref="Wave"/>) e o segredo já REDIGIDO (<see cref="RawSas"/>); <see cref="Scope"/> é resolvido
/// pelo composition root a partir do transporte autenticado (mesmo padrão de
/// <c>SubmitMailboxPrecheckRequest</c>). Nenhum campo secreto aparece em texto claro neste tipo.
/// </summary>
public sealed record IntakePurviewSasRequest(TenantScope Scope, WaveId Wave, RedactedSecret RawSas, CorrelationId Correlation);

/// <summary>
/// Valida (fail-closed), protege via <see cref="ISecretStore"/> e persiste o handle opaco de custódia do
/// SAS para uma wave (work order AB-I5-004). A wave é resolvida a partir de <see cref="IWaveStore"/>
/// (mesma fonte server-side autorizada já usada pelos demais casos de uso Purview) — nunca confia
/// implicitamente que o chamador tem acesso à wave sem essa checagem (anti-IDOR).
/// <para>
/// Um novo intake para uma wave que já possui um handle "vivo" (Stored/Available/Consumed) o substitui
/// EXPLICITAMENTE: o anterior é marcado <see cref="SasHandleState.Destroyed"/> na MESMA operação atômica
/// que insere a nova geração (item 15) — nunca duas gerações vivas simultâneas (item 16). A proteção do
/// segredo (<see cref="ISecretStore.ProtectAsync"/>) ocorre UMA única vez; apenas a gravação do metadado
/// é reexecutada sob corrida (o material órfão de uma tentativa perdida permanece protegido e
/// inacessível — nunca texto claro — até uma rotina de expurgo futura).
/// </para>
/// </summary>
public sealed class IntakePurviewSasUseCase(
    IWaveStore waves, IPurviewSasUploadHandleStore handles, ISecretStore secrets, IClock clock)
{
    /// <summary>Limite de tentativas de convergência sob corrida (mesmo racional dos demais casos de uso Purview).</summary>
    private const int MaxConvergenceAttempts = 8;

    private const string WaveNotFoundMessage =
        "Intake recusado (fail-closed): onda não encontrada em um escopo autorizado do chamador.";

    private readonly IWaveStore _waves = waves;
    private readonly IPurviewSasUploadHandleStore _handles = handles;
    private readonly ISecretStore _secrets = secrets;
    private readonly IClock _clock = clock;

    /// <summary>Valida, protege e persiste o handle de custódia; devolve o handle já em <see cref="SasHandleState.Available"/>.</summary>
    /// <exception cref="PurviewWaveNotFoundException">A wave não existe/não pertence ao escopo do chamador.</exception>
    /// <exception cref="PurviewSasIntakeRejectedException">A URL SAS foi recusada fail-closed pela política de validação.</exception>
    /// <exception cref="ConcurrencyException">Contenção persistente: <see cref="MaxConvergenceAttempts"/> tentativas não convergiram.</exception>
    public async Task<PurviewSasUploadHandle> ExecuteAsync(IntakePurviewSasRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wave = await _waves.GetAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewWaveNotFoundException(WaveNotFoundMessage);

        var now = _clock.UtcNow;
        var validation = PurviewSasIntakePolicy.Validate(request.RawSas.Reveal(), now);
        if (!validation.Accepted)
        {
            throw new PurviewSasIntakeRejectedException(validation.Reason);
        }

        // Protege UMA única vez — retries abaixo disputam somente a gravação do metadado, nunca repetem
        // a proteção do MESMO segredo já validado.
        var secretReference = await _secrets
            .ProtectAsync(request.Scope, validation.Secret!, request.Correlation, cancellationToken)
            .ConfigureAwait(false);

        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            var previous = await _handles.GetCanonicalAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false);
            var nextGeneration = (previous?.Generation ?? 0) + 1;

            var candidate = PurviewSasUploadHandle.Intake(
                SasHandleId.New(), request.Scope.Tenant, request.Scope.Project, request.Wave, nextGeneration,
                validation.Fingerprint!.Value, secretReference, validation.AuthorizedHost!, validation.AuthorizedContainer!,
                keyVersion: null, validation.ExpiresAtUtc!.Value, request.Correlation, now);

            try
            {
                var inserted = await _handles
                    .ReplaceCanonicalAsync(request.Scope, request.Wave, previous, candidate, cancellationToken)
                    .ConfigureAwait(false);
                return await _handles.SaveTransitionAsync(inserted.MarkAvailable(now), cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException) when (attempt < MaxConvergenceAttempts)
            {
                // Outra submissão concorrente já substituiu o canônico — releé e tenta a próxima geração livre.
            }
        }

        throw new ConcurrencyException(
            $"Wave {request.Wave.Value}: não foi possível convergir o intake do SAS após " +
            $"{MaxConvergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tentativas concorrentes.");
    }
}
