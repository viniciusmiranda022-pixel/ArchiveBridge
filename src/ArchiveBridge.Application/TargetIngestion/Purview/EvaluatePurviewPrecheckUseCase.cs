using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview;

/// <summary>Solicitação de avaliação — <see cref="Scope"/> resolvido pelo composition root a partir do transporte autenticado.</summary>
public sealed record EvaluatePurviewPrecheckRequest(TenantScope Scope, WaveId WaveId, CorrelationId Correlation);

/// <summary>Desfecho do gate para UM archive de destino da onda.</summary>
public sealed record ArchivePrecheckOutcome(TargetArchiveId Archive, PurviewPrecheckGateResult Result);

/// <summary>Relatório do gate para TODOS os archives de destino distintos da onda.</summary>
public sealed record WavePrecheckReport(WaveId WaveId, IReadOnlyList<ArchivePrecheckOutcome> PerArchive)
{
    /// <summary>Verdadeiro se QUALQUER archive está bloqueado.</summary>
    public bool AnyBlocked => PerArchive.Any(outcome => !outcome.Result.Allowed);
}

/// <summary>
/// Avalia o precheck/capacity gate do Purview Network Upload (runbook §25.4, work order AB-I5-001) para
/// TODOS os archives de destino distintos de uma onda. Puramente READ-ONLY (work order item 5): lê a onda,
/// a capability evidence e os precheck snapshots JÁ persistidos e aplica <see cref="PurviewPrecheckGate"/> —
/// nunca sonda o adapter nem grava estado. Separar "coletar evidência" (<see cref="DiscoverPurviewCapabilityUseCase"/>/
/// <see cref="SubmitMailboxPrecheckUseCase"/>) de "avaliar o gate contra a evidência mais recente" mantém a
/// avaliação determinística e testável sem I/O externo, mesmo desenho de <c>EvDeltaStrategySelectionPolicy</c>.
/// </summary>
public sealed class EvaluatePurviewPrecheckUseCase(
    IWaveStore waves, ICapabilityEvidenceStore capability, IMailboxPrecheckStore prechecks, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly ICapabilityEvidenceStore _capability = capability;
    private readonly IMailboxPrecheckStore _prechecks = prechecks;
    private readonly IClock _clock = clock;

    /// <summary>Avalia o gate para todos os archives de destino distintos da onda.</summary>
    /// <exception cref="PurviewWaveNotFoundException">Onda inexistente ou fora do escopo.</exception>
    public async Task<WavePrecheckReport> ExecuteAsync(EvaluatePurviewPrecheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wave = await _waves.GetAsync(request.Scope, request.WaveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewWaveNotFoundException("Onda não encontrada no escopo.");

        var now = _clock.UtcNow;
        var latestCapability = await _capability
            .GetLatestAsync(request.Scope, TargetProvider.Purview, PurviewCapabilityRoutes.PstImport, cancellationToken)
            .ConfigureAwait(false);
        var capabilityOutcome = CapabilityEvidencePolicy.EnsureGeneralAvailability(
            latestCapability, now, CapabilityEvidencePolicy.DefaultMaxAge);

        // Cada linha da onda vira uma linha do CSV mapping (runbook §25.8) — o limite de 500 linhas é
        // avaliado sobre a onda inteira, não por archive.
        var csvRowCount = wave.Selection.Entries.Count;
        var limits = PurviewPolicyLimits.RunbookDefault;

        // A capability é da ROTA (tenant/projeto-wide), não por archive: se ela já bloqueia, TODOS os
        // archives da onda são bloqueados pelo MESMO motivo — sem exigir um precheck por archive só para
        // descobrir isso (a capability é sempre a primeira checagem do gate, mesma ordem de
        // PurviewPrecheckGate.EvaluateArchiveImport).
        var capabilityBlock = PurviewPrecheckGate.EvaluateCapabilityOnly(capabilityOutcome);

        var outcomes = new List<ArchivePrecheckOutcome>();
        foreach (var group in wave.Selection.Entries
                     .GroupBy(entry => entry.Archive.Identity)
                     .OrderBy(group => group.Key.Value, StringComparer.Ordinal))
        {
            if (capabilityBlock is not null)
            {
                outcomes.Add(new ArchivePrecheckOutcome(group.Key, capabilityBlock));
                continue;
            }

            var plannedArchiveImportBytes = group.Sum(entry => entry.SizeBytes);
            var plannedPartSizesBytes = group.Select(entry => entry.SizeBytes).ToArray();

            var precheckSnapshot = await _prechecks.GetLatestAsync(request.Scope, group.Key, cancellationToken).ConfigureAwait(false);
            var result = precheckSnapshot is null
                ? PurviewPrecheckGateResult.Block(PurviewPrecheckBlockReason.MailboxPrecheckMissing, "MAILBOX_PRECHECK_MISSING")
                : PurviewPrecheckGate.EvaluateArchiveImport(
                    limits, capabilityOutcome, precheckSnapshot, csvRowCount, plannedArchiveImportBytes, plannedPartSizesBytes);

            outcomes.Add(new ArchivePrecheckOutcome(group.Key, result));
        }

        return new WavePrecheckReport(request.WaveId, outcomes);
    }
}
