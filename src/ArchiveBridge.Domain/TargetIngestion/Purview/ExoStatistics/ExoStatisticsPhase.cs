namespace ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;

/// <summary>
/// Fase de captura da observação de estatísticas do archive EXO (runbook §25.2/§26.2, AB-I6-005 item 1).
/// Read-only em ambas as fases — nenhum valor representa resultado de reconciliação, disposition ou
/// conclusão de onda/projeto (STOP-THE-LINE do work order).
/// </summary>
public enum ExoStatisticsPhase
{
    /// <summary>Estado do archive ANTES da importação observada (runbook §25.2) — linha de base real.</summary>
    BeforeImport,

    /// <summary>
    /// Estado do archive DEPOIS da importação observada (runbook §26.2) — só aceito quando existe
    /// evidência canônica suficiente de que a etapa de import concluiu (item 4). Nunca fecha a onda/
    /// projeto, mesmo quando a evidência subjacente é <c>ImportCompleted</c> (runbook §25.9 item 81).
    /// </summary>
    AfterImport,
}
