using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Captura estatísticas do archive EXO (before/after) via <see cref="IExoArchiveStatisticsAdapter"/> (porta
/// substituível, somente leitura) e persiste a observação normalizada/canonicalizada (AB-I6-005 itens 1-4).
/// <para>
/// ANTES de sondar o adapter, resolve a <see cref="Waves.ArchiveRef"/> canônica a partir do
/// <see cref="IWaveStore"/> (mesmo desenho anti-IDOR de <c>SubmitMailboxPrecheckUseCase</c>, AB-I5-003) —
/// nunca confia numa identidade "resolvida" auto-declarada pelo chamador. Onda inexistente, archive fora
/// da seleção da onda ou archive ainda sem identidade resolvida produzem TODOS o mesmo
/// <see cref="ExoArchiveStatisticsSourceNotFoundException"/>, sem sondar o adapter e sem vazar existência/
/// UPN/GUID/detalhes cross-tenant/project (item 13).
/// </para>
/// <para>
/// <see cref="ExecuteBeforeImportAsync"/> aplica o gate temporal do baseline (AB-I6-006): antes de sondar o
/// adapter, verifica TODOS os planos de import job já existentes para a onda
/// (<see cref="IPurviewImportJobStore.GetPlansForWaveAsync"/>) e recusa fail-closed
/// (<see cref="ExoArchiveStatisticsPrerequisiteException"/>) se a observação mais recente de QUALQUER um
/// deles já indicar execução de import iniciada/concluída/falha
/// (<see cref="ExoBeforeImportEligibility.IsImportExecutionStartedOrBeyond"/>) — a decisão vem
/// inteiramente de evidência server-side, nunca de um identificador ou timestamp fornecido pelo chamador.
/// Um baseline já capturado ANTES desse boundary continua legível/revalidável normalmente
/// (<c>GetLatestAsync</c>/<c>GetFoldersAsync</c> não são afetados por este gate); apenas uma NOVA captura
/// depois do boundary é bloqueada, e nenhuma versão N+1 é criada quando isso acontece.
/// </para>
/// <para>
/// <see cref="ExecuteAfterImportAsync"/> reaproveita <see cref="EvaluatePurviewServiceResultCompletenessUseCase"/>
/// (Passo 1) como a evidência canônica de conclusão do import exigida pelo item 4/critério de aceite 2: o
/// adapter só é sondado quando o desfecho é
/// <see cref="PurviewServiceResultCompletenessOutcome.CompleteForProviderEvidence"/> — nunca antes.
/// <c>Complete</c>/<c>ImportCompleted</c> permanece apenas evidência do provider; nenhuma chamada deste
/// caso de uso fecha a onda/projeto ou produz um resultado de reconciliação.
/// </para>
/// </summary>
public sealed class CaptureExoArchiveStatisticsUseCase(
    IWaveStore waves,
    IPurviewImportJobStore jobs,
    IExoArchiveStatisticsAdapter adapter,
    IExoArchiveStatisticsStore snapshots,
    EvaluatePurviewServiceResultCompletenessUseCase completeness,
    IClock clock)
{
    private const string ArchiveNotFoundMessage =
        "Captura de estatísticas EXO recusada (fail-closed): archive de destino não encontrado em uma onda " +
        "autorizada no escopo do chamador.";

    private readonly IWaveStore _waves = waves;
    private readonly IPurviewImportJobStore _jobs = jobs;
    private readonly IExoArchiveStatisticsAdapter _adapter = adapter;
    private readonly IExoArchiveStatisticsStore _snapshots = snapshots;
    private readonly EvaluatePurviewServiceResultCompletenessUseCase _completeness = completeness;
    private readonly IClock _clock = clock;

    /// <summary>
    /// Captura <see cref="ExoStatisticsPhase.BeforeImport"/> — representa o estado anterior real. Só é
    /// aceita enquanto NENHUM plano de import job desta onda tiver evidência observada de execução de
    /// import iniciada/concluída/falha (AB-I6-006) — o adapter NUNCA é sondado quando esse boundary já foi
    /// cruzado.
    /// </summary>
    /// <exception cref="ExoArchiveStatisticsSourceNotFoundException">Onda/archive inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="ExoArchiveStatisticsPrerequisiteException">A execução do import já começou/terminou para algum plano desta onda.</exception>
    /// <exception cref="ExoArchiveStatisticsValidationException">Estatística de pasta inválida/oversized/duplicada retornada pelo adapter.</exception>
    public Task<ExoArchiveStatisticsSnapshot> ExecuteBeforeImportAsync(
        TenantScope scope, WaveId waveId, TargetArchiveId archive, CorrelationId correlation, CancellationToken cancellationToken) =>
        CaptureAsync(scope, waveId, archive, ExoStatisticsPhase.BeforeImport, plannedJobName: null, correlation, cancellationToken);

    /// <summary>
    /// Captura <see cref="ExoStatisticsPhase.AfterImport"/> — só aceita quando existe evidência canônica de
    /// que o provider já cobriu 100% dos PSTs canônicos da onda com status/contadores conclusivos
    /// (critério de aceite 2). O adapter NUNCA é chamado antes dessa verificação passar.
    /// </summary>
    /// <exception cref="ExoArchiveStatisticsSourceNotFoundException">Onda/archive/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="ExoArchiveStatisticsPrerequisiteException">Evidência de conclusão do import ainda insuficiente.</exception>
    /// <exception cref="ExoArchiveStatisticsValidationException">Estatística de pasta inválida/oversized/duplicada retornada pelo adapter.</exception>
    public Task<ExoArchiveStatisticsSnapshot> ExecuteAfterImportAsync(
        TenantScope scope,
        WaveId waveId,
        TargetArchiveId archive,
        PurviewImportJobName plannedJobName,
        CorrelationId correlation,
        CancellationToken cancellationToken) =>
        CaptureAsync(scope, waveId, archive, ExoStatisticsPhase.AfterImport, plannedJobName, correlation, cancellationToken);

    private async Task<ExoArchiveStatisticsSnapshot> CaptureAsync(
        TenantScope scope,
        WaveId waveId,
        TargetArchiveId archive,
        ExoStatisticsPhase phase,
        PurviewImportJobName? plannedJobName,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new ExoArchiveStatisticsSourceNotFoundException(ArchiveNotFoundMessage);

        // Resolve a ArchiveRef CANÔNICA a partir da seleção JÁ persistida da onda sob TenantScope — a
        // instância efetivamente sondada é a do estado server-side, nunca uma reconstruída a partir de
        // campos fornecidos pelo caller (item 2/13). Um archive fora da seleção e um archive presente mas
        // ainda sem identidade resolvida por um manifesto autorizado falham EXATAMENTE do mesmo jeito.
        var canonicalEntry = wave.Selection.Entries.FirstOrDefault(entry => entry.Archive.Identity.Equals(archive));
        if (canonicalEntry is null || !canonicalEntry.Archive.IsIdentityResolved)
        {
            throw new ExoArchiveStatisticsSourceNotFoundException(ArchiveNotFoundMessage);
        }

        var mailbox = canonicalEntry.Archive;

        if (phase == ExoStatisticsPhase.BeforeImport)
        {
            // AB-I6-006: um baseline BeforeImport só é defensável enquanto NENHUM plano desta onda tiver
            // evidência observada de que a execução do import (não apenas planejamento/validação) já
            // começou — verificado ANTES de qualquer contato com o adapter, a partir de evidência
            // inteiramente server-side (nunca de um identificador/timestamp fornecido pelo chamador).
            var plans = await _jobs.GetPlansForWaveAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
            foreach (var plan in plans)
            {
                var latestObservation = await _jobs
                    .GetLatestObservationAsync(scope, waveId, plan.PlannedJobName, cancellationToken)
                    .ConfigureAwait(false);
                if (latestObservation is not null && ExoBeforeImportEligibility.IsImportExecutionStartedOrBeyond(latestObservation.ObservedStatus))
                {
                    throw new ExoArchiveStatisticsPrerequisiteException(
                        "Captura BeforeImport recusada (fail-closed): já existe evidência canônica de que a execução do " +
                        $"import desta onda começou (plano {plan.PlannedJobName.Value}, status observado " +
                        $"{latestObservation.ObservedStatus}) — um baseline anterior ao import não é mais defensável.");
                }
            }
        }

        if (phase == ExoStatisticsPhase.AfterImport)
        {
            // Critério de aceite 2: a verificação de completude roda ANTES de qualquer contato com o
            // adapter — nenhuma sondagem é feita quando a evidência de conclusão ainda é insuficiente.
            var assessment = await _completeness
                .ExecuteAsync(scope, waveId, plannedJobName!.Value, cancellationToken)
                .ConfigureAwait(false);
            if (assessment.Outcome != PurviewServiceResultCompletenessOutcome.CompleteForProviderEvidence)
            {
                throw new ExoArchiveStatisticsPrerequisiteException(
                    "Captura AfterImport recusada (fail-closed): a evidência de conclusão do import ainda não cobre " +
                    "100% dos PSTs canônicos da onda com status/contadores conclusivos (estado atual: " +
                    $"{assessment.Outcome}).");
            }
        }

        var observation = await _adapter.ObserveAsync(scope, mailbox, phase, correlation, cancellationToken).ConfigureAwait(false);
        var canonicalFolders = ExoArchiveFolderStatisticsSet.Canonicalize(ToDomainFolders(observation.Folders));

        return await _snapshots.PersistAsync(
            scope,
            waveId,
            archive,
            phase,
            observation.ArchiveStatus,
            observation.ExchangeGuid,
            observation.ArchiveGuid,
            observation.ItemCount,
            observation.TotalItemSizeBytes,
            observation.TotalDeletedItemSizeBytes,
            observation.LastLogonTimeUtc,
            observation.RetentionHoldEnabled,
            observation.LitigationHoldEnabled,
            observation.AutoExpandingArchiveEnabled,
            canonicalFolders,
            observation.ObservedAtUtc,
            correlation,
            _clock.UtcNow,
            fence: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static List<ExoArchiveFolderStatistic> ToDomainFolders(IReadOnlyList<ExoArchiveFolderStatisticObservation> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var result = new List<ExoArchiveFolderStatistic>(folders.Count);
        foreach (var folder in folders)
        {
            result.Add(new ExoArchiveFolderStatistic(
                folder.FolderPath,
                folder.FolderType,
                folder.ItemsInFolder,
                folder.ItemsInFolderAndSubfolders,
                folder.FolderSizeBytes,
                folder.FolderAndSubfolderSizeBytes,
                folder.OldestItemReceivedDateUtc,
                folder.NewestItemReceivedDateUtc));
        }

        return result;
    }
}
