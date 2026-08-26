using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Compõe o read model de backlog de exceções de uma wave (item 14 do work order AB-I6-010) a partir dos
/// itens da avaliação de reconciliação VIGENTE (Passo 3) e das decisões vigentes já persistidas — leitura
/// pura, sem RBAC adicional além do já aplicado nas telas do portal para leitura geral (mesmo padrão de
/// <c>ReconciliationWaveSummary</c>). Nunca calcula/expõe um resultado terminal de projeto (STOP-THE-LINE).
/// </summary>
public sealed class GetReconciliationExceptionBacklogUseCase(
    IReconciliationAssessmentStore assessments,
    IReconciliationExceptionDispositionStore dispositions)
{
    private readonly IReconciliationAssessmentStore _assessments = assessments;
    private readonly IReconciliationExceptionDispositionStore _dispositions = dispositions;

    /// <summary><see langword="null"/> quando a onda/plano é inexistente/fora de escopo, ou nenhuma avaliação ainda foi computada.</summary>
    public async Task<ReconciliationExceptionWaveBacklog?> ExecuteAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        var latest = await _assessments.GetLatestAsync(scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return null;
        }

        var pstItems = await _assessments.GetPstItemsAsync(scope, wave, plannedJobName, latest.AssessmentVersion, cancellationToken).ConfigureAwait(false);
        var archiveItems = await _assessments.GetArchiveItemsAsync(scope, wave, plannedJobName, latest.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        var currentDecisions = await _dispositions
            .GetCurrentDecisionsForAssessmentAsync(scope, wave, plannedJobName, latest.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);

        return ReconciliationExceptionWaveBacklog.From(latest.AssessmentVersion, pstItems, archiveItems, currentDecisions);
    }
}
