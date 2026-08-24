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
/// Um novo intake para uma wave que já possui um handle "vivo" (Stored/Available/Claimed/Consumed) o
/// substitui EXPLICITAMENTE: o anterior é marcado <see cref="SasHandleState.Destroyed"/> na MESMA operação
/// atômica que insere a nova geração (item 15) — nunca duas gerações vivas simultâneas (item 16). A
/// proteção do segredo (<see cref="ISecretStore.ProtectAsync"/>) ocorre UMA única vez; apenas a gravação do
/// metadado é reexecutada sob corrida.
/// </para>
/// <para>
/// Lifecycle crash-consistente do material secreto (AB-I5-006 item 3): (a) se NENHUMA tentativa de
/// convergência tiver sucesso, ou uma exceção/cancelamento interromper o fluxo ANTES de o candidato se
/// tornar o canônico persistido, o material recém-protegido é destruído por compensação (best-effort,
/// nunca mascara a exceção original) — nunca deixado permanentemente órfão sem que uma compensação tenha
/// sido tentada; (b) uma vez que o candidato SE TORNA o canônico persistido, o material da geração anterior
/// substituída (já <see cref="SasHandleState.Destroyed"/> na mesma transação) também é destruído por
/// compensação — permanece rastreável através da própria linha Destroyed (que retém
/// <see cref="PurviewSasUploadHandle.SecretStoreReference"/>) mesmo se a compensação falhar.
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

        // Torna-se 'true' assim que o candidato se torna o canônico persistido (ReplaceCanonicalAsync já
        // retornou com sucesso) — a partir daí o material NUNCA é compensado por este método, mesmo que
        // MarkAvailable falhe depois (o handle fica recuperável em Stored, referenciando material legítimo,
        // nunca órfão).
        var candidateCommitted = false;
        try
        {
            for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
            {
                var previous = await _handles.GetCanonicalAsync(request.Scope, request.Wave, cancellationToken).ConfigureAwait(false);
                var nextGeneration = (previous?.Generation ?? 0) + 1;

                var candidate = PurviewSasUploadHandle.Intake(
                    SasHandleId.New(), request.Scope.Tenant, request.Scope.Project, request.Wave, nextGeneration,
                    validation.Fingerprint!.Value, secretReference, validation.AuthorizedHost!, validation.AuthorizedContainer!,
                    keyVersion: null, validation.ExpiresAtUtc!.Value, request.Correlation, now);

                PurviewSasUploadHandle inserted;
                try
                {
                    inserted = await _handles
                        .ReplaceCanonicalAsync(request.Scope, request.Wave, previous, candidate, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ConcurrencyException) when (attempt < MaxConvergenceAttempts)
                {
                    // Outra submissão concorrente já substituiu o canônico — releé e tenta a próxima geração livre.
                    continue;
                }

                candidateCommitted = true;

                if (previous is not null)
                {
                    // Geração anterior já Destroyed no metadado (mesma transação atômica do insert acima,
                    // item 15) — destrói o material correspondente por compensação (item 3). Best-effort:
                    // nunca desfaz o intake bem-sucedido, que já é o canônico durável.
                    await TryDestroyOrphanedMaterialAsync(request.Scope, previous.SecretStoreReference, request.Correlation, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return await _handles.SaveTransitionAsync(inserted.MarkAvailable(now), cancellationToken).ConfigureAwait(false);
            }

            throw new ConcurrencyException(
                $"Wave {request.Wave.Value}: não foi possível convergir o intake do SAS após " +
                $"{MaxConvergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tentativas concorrentes.");
        }
        catch (Exception) when (!candidateCommitted)
        {
            // Nenhuma tentativa convergiu (contenção persistente) OU uma exceção inesperada/cancelamento
            // interrompeu o fluxo ANTES de o candidato se tornar canônico — o material recém-protegido
            // nunca chegou a ser referenciado por nenhum handle persistido: destrói por compensação
            // (best-effort; NUNCA mascara a exceção original) em vez de deixá-lo órfão sem tentativa de
            // limpeza (AB-I5-006 item 3).
            await TryDestroyOrphanedMaterialAsync(request.Scope, secretReference, request.Correlation, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    // Compensação best-effort de material que não deve mais permanecer referenciável (candidato que nunca
    // convergiu, ou geração anterior já substituída). Uma falha aqui NUNCA decide o resultado da operação
    // principal (nem mascara a exceção original, nem desfaz um intake já persistido com sucesso) — o
    // material permanece rastreável via a referência já gravada (na linha Destroyed correspondente, no caso
    // da geração anterior) para um expurgo futuro fora do escopo deste Passo.
    private async Task TryDestroyOrphanedMaterialAsync(
        TenantScope scope, SecretStoreHandleReference reference, CorrelationId correlation, CancellationToken cancellationToken)
    {
        try
        {
            await _secrets.DestroyAsync(scope, reference, correlation, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — ver comentário acima do chamador.
        }
    }
}
