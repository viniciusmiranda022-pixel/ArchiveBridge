using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Regra de elegibilidade temporal do baseline <see cref="ExoStatisticsPhase.BeforeImport"/> (AB-I6-006):
/// um snapshot rotulado <c>BeforeImport</c> só é defensável como "estado anterior real" (work order
/// AB-I6-005 item 5) enquanto NENHUMA evidência canônica indicar que a execução do import já começou para
/// a onda. Função pura e determinística — nunca consulta stores, nunca tem efeito colateral.
/// <para>
/// O boundary escolhido é o estado observado MAIS PRECOCE e inequívoco que indica início real de
/// <c>Import data</c> (runbook §25.9 item 79): <see cref="PurviewImportJobObservedStatus.ImportStarted"/>.
/// Estados anteriores (<see cref="PurviewImportJobObservedStatus.JobCreated"/>,
/// <see cref="PurviewImportJobObservedStatus.ValidationAttached"/>,
/// <see cref="PurviewImportJobObservedStatus.AnalysisCompleted"/>) representam apenas planejamento/
/// validação do mapping — o archive de destino ainda não foi tocado pelo import, então um baseline
/// capturado nesse ponto continua sendo o "estado anterior" legítimo.
/// <see cref="PurviewImportJobObservedStatus.ImportFailed"/> também bloqueia: mesmo uma falha indica que a
/// execução chegou a começar, podendo ter alterado parcialmente o archive.
/// </para>
/// </summary>
public static class ExoBeforeImportEligibility
{
    /// <summary>
    /// Verdadeiro quando <paramref name="status"/> indica que a execução do import (não apenas
    /// planejamento/validação) já começou ou terminou para o plano observado — um novo baseline
    /// <c>BeforeImport</c> não pode mais ser capturado a partir deste ponto.
    /// </summary>
    public static bool IsImportExecutionStartedOrBeyond(PurviewImportJobObservedStatus status) =>
        status is PurviewImportJobObservedStatus.ImportStarted
            or PurviewImportJobObservedStatus.ImportCompleted
            or PurviewImportJobObservedStatus.ImportFailed;
}
