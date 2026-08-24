using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>
/// Solicitação de descoberta de capability — <see cref="Scope"/> é resolvido pelo composition root a
/// partir do transporte autenticado, nunca informado livremente pelo chamador.
/// </summary>
public sealed record DiscoverPurviewCapabilityRequest(TenantScope Scope, PurviewCapabilityRoute Route, CorrelationId Correlation);

/// <summary>
/// Consulta o catálogo EMBARCADO de capability (<see cref="PurviewCapabilityCatalog"/> — nunca uma chamada
/// em tempo real ao fornecedor, work order item 1), normaliza em <see cref="CapabilityEvidence"/> e decide,
/// ANTES de qualquer escrita, se o resultado é idêntico à última evidência persistida (réplay idempotente,
/// nenhuma linha nova) ou se representa mudança real (nova versão, evidência anterior nunca reescrita —
/// work order item 11). Mesmo desenho de <c>SubmitInventorySnapshotUseCase</c> (Slice 4C, AB-4C-002).
/// </summary>
public sealed class DiscoverPurviewCapabilityUseCase(ICapabilityEvidenceStore evidence, IClock clock)
{
    /// <summary>
    /// Limite de tentativas de convergência sob corrida: cada tentativa releé o latest e tenta a próxima
    /// versão livre. Alto o bastante para absorver contenção real entre poucos writers concorrentes do
    /// MESMO escopo/rota, baixo o bastante para falhar fechado (nunca travar indefinidamente).
    /// </summary>
    private const int MaxConvergenceAttempts = 8;

    private readonly ICapabilityEvidenceStore _evidence = evidence;
    private readonly IClock _clock = clock;

    /// <summary>Descobre, normaliza e persiste (ou converge por réplay) a capability evidence da rota.</summary>
    /// <exception cref="ConcurrencyException">Contenção persistente: <see cref="MaxConvergenceAttempts"/> tentativas não convergiram.</exception>
    public async Task<CapabilityEvidenceAppendResult> ExecuteAsync(
        DiscoverPurviewCapabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A consulta ao catálogo (única, não repetida por tentativa de convergência) é o único "contato"
        // com a fonte de verdade — retries abaixo disputam apenas a VERSÃO de persistência do mesmo fato já
        // consultado, nunca reconsultam o catálogo.
        var entry = PurviewCapabilityCatalog.Describe(request.Route);
        var now = _clock.UtcNow;

        for (var attempt = 1; attempt <= MaxConvergenceAttempts; attempt++)
        {
            var latest = await _evidence.GetLatestAsync(request.Scope, TargetProvider.Purview, request.Route, cancellationToken)
                .ConfigureAwait(false);
            var candidateVersion = (latest?.Version ?? 0) + 1;
            var candidate = CapabilityEvidence.Record(
                CapabilityEvidenceId.New(), request.Scope.Tenant, request.Scope.Project, TargetProvider.Purview, request.Route,
                candidateVersion, entry.Status, entry.SourceReference, entry.DocumentationVersion, entry.CapabilityVersionLabel,
                entry.AsOfUtc, request.Correlation, now);

            if (latest is not null && latest.IsSameContentAs(candidate))
            {
                // Réplay idempotente: nenhuma mudança real, nenhuma linha nova.
                return new CapabilityEvidenceAppendResult(latest, Created: false);
            }

            try
            {
                return await _evidence.AppendAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException) when (attempt < MaxConvergenceAttempts)
            {
                // Outra descoberta concorrente ocupou candidateVersion com conteúdo DIFERENTE — releé o
                // latest agora atualizado e tenta de novo com a próxima versão livre.
            }
        }

        throw new ConcurrencyException(
            $"Rota {request.Route.Value}: não foi possível convergir a capability evidence após " +
            $"{MaxConvergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tentativas concorrentes.");
    }
}
