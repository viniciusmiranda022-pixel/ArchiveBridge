using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>
/// Solicitação de submissão de precheck — <see cref="Scope"/> é resolvido pelo composition root a partir
/// do transporte autenticado; <see cref="Mailbox"/> deve carregar identidade JÁ resolvida server-side por
/// um manifesto/resolvedor autorizado (anti-IDOR, work order item 10) — nunca um UPN cru enviado livremente
/// pelo chamador.
/// </summary>
public sealed record SubmitMailboxPrecheckRequest(TenantScope Scope, ArchiveRef Mailbox, CorrelationId Correlation);

/// <summary>
/// Sonda o precheck via <see cref="IMailboxPrecheckAdapter"/> (porta substituível, somente leitura), normaliza
/// em <see cref="MailboxPrecheckSnapshot"/> e decide, ANTES de qualquer escrita, se o resultado é idêntico ao
/// último snapshot persistido (réplay idempotente) ou se representa mudança real (nova versão, evidência
/// anterior nunca reescrita — work order item 11). Mesmo desenho de <c>SubmitInventorySnapshotUseCase</c>.
/// Nenhuma mutação de tenant/mailbox é executada por este caso de uso (work order item 5).
/// </summary>
public sealed class SubmitMailboxPrecheckUseCase(IMailboxPrecheckStore prechecks, IMailboxPrecheckAdapter adapter, IClock clock)
{
    /// <summary>Limite de tentativas de convergência sob corrida (mesmo racional de <see cref="DiscoverPurviewCapabilityUseCase"/>).</summary>
    private const int MaxConvergenceAttempts = 8;

    private readonly IMailboxPrecheckStore _prechecks = prechecks;
    private readonly IMailboxPrecheckAdapter _adapter = adapter;
    private readonly IClock _clock = clock;

    /// <summary>Sonda, normaliza e persiste (ou converge por réplay) o precheck da mailbox de destino.</summary>
    /// <exception cref="PurviewValidationException"><see cref="SubmitMailboxPrecheckRequest.Mailbox"/> não tem identidade resolvida.</exception>
    /// <exception cref="ConcurrencyException">Contenção persistente: <see cref="MaxConvergenceAttempts"/> tentativas não convergiram.</exception>
    public async Task<MailboxPrecheckAppendResult> ExecuteAsync(SubmitMailboxPrecheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Mailbox.IsIdentityResolved)
        {
            throw new PurviewValidationException(
                "Precheck recusado (fail-closed): a identidade do archive de destino não foi resolvida " +
                "server-side por um manifesto/resolvedor autorizado.");
        }

        // A sondagem em si (única, não repetida por tentativa de convergência) é o único contato com o
        // adapter — retries abaixo disputam apenas a VERSÃO de persistência do mesmo resultado já sondado.
        var observation = await _adapter.ObserveAsync(request.Scope, request.Mailbox, request.Correlation, cancellationToken)
            .ConfigureAwait(false);
        var now = _clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            var latest = await _prechecks.GetLatestAsync(request.Scope, request.Mailbox.Identity, cancellationToken).ConfigureAwait(false);
            var candidateVersion = (latest?.Version ?? 0) + 1;
            var candidate = MailboxPrecheckSnapshot.Observe(
                PrecheckSnapshotId.New(), request.Scope.Tenant, request.Scope.Project, request.Mailbox, candidateVersion,
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
            $"Archive {request.Mailbox.Identity.Value}: não foi possível convergir o precheck após " +
            $"{MaxConvergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tentativas concorrentes.");
    }
}
