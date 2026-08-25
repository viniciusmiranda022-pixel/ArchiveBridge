namespace ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Ponto de progresso do import job OBSERVADO pelo operador no portal Purview (runbook §25.9 itens 74-81)
/// — evidência transcrita manualmente, NUNCA inferida/derivada por automação de portal (STOP-THE-LINE do
/// AB-I6-001). Nenhum destes valores encerra a onda/projeto: mesmo <see cref="ImportCompleted"/> é apenas
/// "o serviço reportou conclusão" (runbook §25.9 item 81: "`Complete` não fecha o projeto"; AB-I6-001
/// invariantes) — o fechamento depende de reconciliação expected-vs-observed, disposition e certificate,
/// que pertencem a Passos posteriores do EPIC-07.
/// </summary>
public enum PurviewImportJobObservedStatus
{
    /// <summary>Job criado no portal com o nome planejado (runbook item 74, "salvar o job").</summary>
    JobCreated,

    /// <summary>Validation report anexado ao job da plataforma (runbook item 72).</summary>
    ValidationAttached,

    /// <summary>Portal reportou "Analysis completed" (runbook item 76) — apenas evidência observada.</summary>
    AnalysisCompleted,

    /// <summary>Operador iniciou "Import data" (runbook item 79) — apenas evidência observada.</summary>
    ImportStarted,

    /// <summary>Portal reportou conclusão do import (runbook item 81) — NUNCA fecha a onda/projeto.</summary>
    ImportCompleted,

    /// <summary>Portal reportou falha/cancelamento do import job.</summary>
    ImportFailed,
}
