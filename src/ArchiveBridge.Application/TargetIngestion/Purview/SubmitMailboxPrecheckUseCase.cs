using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>
/// Solicitação de submissão de precheck — <see cref="Scope"/> é resolvido pelo composition root a partir
/// do transporte autenticado. Carrega SOMENTE identificadores opacos (<see cref="WaveId"/> +
/// <see cref="Archive"/>) suficientes para localizar o recurso autorizado; NÃO carrega uma
/// <see cref="ArchiveRef"/> fornecida diretamente pelo chamador — uma <c>ArchiveRef</c> fabricada pelo
/// caller provaria apenas a FORMA do objeto (<c>IsIdentityResolved == true</c> por construção pública),
/// nunca a autorização/proveniência da mailbox (anti-IDOR, work order item 10 / AB-I5-003). O caso de uso
/// resolve a <c>ArchiveRef</c> canônica a partir da seleção da onda JÁ persistida sob <see cref="Scope"/>.
/// </summary>
public sealed record SubmitMailboxPrecheckRequest(TenantScope Scope, WaveId WaveId, TargetArchiveId Archive, CorrelationId Correlation);

/// <summary>
/// Sonda o precheck via <see cref="IMailboxPrecheckAdapter"/> (porta substituível, somente leitura), normaliza
/// em <see cref="MailboxPrecheckSnapshot"/> e decide, ANTES de qualquer escrita, se o resultado é idêntico ao
/// último snapshot persistido (réplay idempotente) ou se representa mudança real (nova versão, evidência
/// anterior nunca reescrita — work order item 11). Mesmo desenho de <c>SubmitInventorySnapshotUseCase</c>.
/// Nenhuma mutação de tenant/mailbox é executada por este caso de uso (work order item 5).
/// <para>
/// ANTES de sondar o adapter, resolve a <see cref="Waves.ArchiveRef"/> canônica a partir do
/// <see cref="IWaveStore"/> (mesma fonte server-side já autorizada usada por
/// <see cref="EvaluatePurviewPrecheckUseCase"/>) — nunca confia numa identidade "resolvida" auto-declarada
/// pelo chamador (anti-IDOR, AB-I5-003). Onda inexistente, archive fora da seleção da onda ou archive
/// ainda sem identidade resolvida produzem TODOS o mesmo <see cref="PurviewArchiveNotFoundException"/>,
/// sem sondar o adapter e sem vazar existência/UPN/GUID/detalhes cross-tenant/project.
/// </para>
/// </summary>
public sealed class SubmitMailboxPrecheckUseCase(
    IWaveStore waves, IMailboxPrecheckStore prechecks, IMailboxPrecheckAdapter adapter, IClock clock)
{
    /// <summary>Limite de tentativas de convergência sob corrida (mesmo racional de <see cref="DiscoverPurviewCapabilityUseCase"/>).</summary>
    private const int MaxConvergenceAttempts = 8;

    private const string ArchiveNotFoundMessage =
        "Precheck recusado (fail-closed): archive de destino não encontrado em uma onda autorizada no " +
        "escopo do chamador.";

    private readonly IWaveStore _waves = waves;
    private readonly IMailboxPrecheckStore _prechecks = prechecks;
    private readonly IMailboxPrecheckAdapter _adapter = adapter;
    private readonly IClock _clock = clock;

    /// <summary>Resolve a mailbox canônica, sonda, normaliza e persiste (ou converge por réplay) o precheck.</summary>
    /// <exception cref="PurviewArchiveNotFoundException">
    /// Onda inexistente/fora do escopo, ou <see cref="SubmitMailboxPrecheckRequest.Archive"/> não pertence à
    /// seleção da onda com identidade já resolvida.
    /// </exception>
    /// <exception cref="ConcurrencyException">Contenção persistente: <see cref="MaxConvergenceAttempts"/> tentativas não convergiram.</exception>
    public async Task<MailboxPrecheckAppendResult> ExecuteAsync(SubmitMailboxPrecheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wave = await _waves.GetAsync(request.Scope, request.WaveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewArchiveNotFoundException(ArchiveNotFoundMessage);

        // Resolve a ArchiveRef CANÔNICA a partir da seleção JÁ persistida da onda sob TenantScope — a
        // instância efetivamente sondada/persistida é a do estado server-side, nunca uma reconstruída a
        // partir de campos fornecidos pelo caller. Um archive fora da seleção (arbitrário/cross-project) e
        // um archive presente mas ainda não resolvido por um manifesto autorizado falham EXATAMENTE do
        // mesmo jeito.
        var canonicalEntry = wave.Selection.Entries.FirstOrDefault(entry => entry.Archive.Identity.Equals(request.Archive));
        if (canonicalEntry is null || !canonicalEntry.Archive.IsIdentityResolved)
        {
            throw new PurviewArchiveNotFoundException(ArchiveNotFoundMessage);
        }

        var mailbox = canonicalEntry.Archive;

        // A sondagem em si (única, não repetida por tentativa de convergência) é o único contato com o
        // adapter — retries abaixo disputam apenas a VERSÃO de persistência do mesmo resultado já sondado.
        var observation = await _adapter.ObserveAsync(request.Scope, mailbox, request.Correlation, cancellationToken)
            .ConfigureAwait(false);
        var now = _clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            var latest = await _prechecks.GetLatestAsync(request.Scope, mailbox.Identity, cancellationToken).ConfigureAwait(false);
            var candidateVersion = (latest?.Version ?? 0) + 1;
            var candidate = MailboxPrecheckSnapshot.Observe(
                PrecheckSnapshotId.New(), request.Scope.Tenant, request.Scope.Project, mailbox, candidateVersion,
                observation.ExchangeGuid, observation.ArchiveGuid, observation.ArchiveStatus, observation.RecipientTypeDetails,
                observation.AutoExpandingArchiveEnabled, observation.LitigationHoldEnabled, observation.RetentionHoldEnabled,
                observation.ArchiveItemCount, observation.ArchiveTotalSizeBytes, observation.ObservedAvailableBytes,
                observation.ObservedAtUtc, request.Correlation, now);

            if (latest is not null && latest.IsSameContentAs(candidate))
            {
                // Réplay idempotente: nenhuma mudança real, nenhuma linha nova.
                return new MailboxPrecheckAppendResult(latest, Created: false);
            }

            try
            {
                return await _prechecks.AppendAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException) when (attempt < MaxConvergenceAttempts)
            {
                // Outra submissão concorrente ocupou candidateVersion com conteúdo DIFERENTE — releé o
                // latest agora atualizado e tenta de novo com a próxima versão livre.
            }
        }

        throw new ConcurrencyException(
            $"Archive {mailbox.Identity.Value}: não foi possível convergir o precheck após " +
            $"{MaxConvergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tentativas concorrentes.");
    }
}
