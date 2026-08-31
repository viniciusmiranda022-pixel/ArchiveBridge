using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de resolução do cenário CANARY.RECONCILIATION_EVIDENCE_PACKAGE (AB-I8-004 escopo obrigatório item
/// 10) — escopado à onda/job específico do canário controlado, diferente dos demais cenários SystemDerived
/// (que não dependem de uma onda).
/// </summary>
public sealed record ResolveCanaryReconciliationEvidenceCommand(
    TenantScope Scope, int PlanVersion, WaveId Wave, PurviewImportJobName PlannedJobName, CorrelationId Correlation);

/// <summary>
/// Resolve e persiste o cenário <c>CANARY.RECONCILIATION_EVIDENCE_PACKAGE</c> a partir do reconciliation
/// certificate canônico e vigente da onda/job do canário (I6) via <see cref="CanaryScenarioEvidenceResolvers"/>.
/// Escopado a UMA versão específica e VIGENTE do plano. NUNCA marca canário/go-live/projeto concluído, NUNCA
/// escreve em Purview/EXO/Graph/EV/AzCopy/host real (STOP-THE-LINE).
/// </summary>
public sealed class ResolveCanaryReconciliationEvidenceUseCase(
    IReconciliationCertificateStore certificateStore,
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(ResolveCanaryReconciliationEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = await CanaryScenarioEvidenceResolvers.ResolveReconciliationEvidencePackageAsync(
            certificateStore, command.Scope, command.Wave, command.PlannedJobName, now, cancellationToken).ConfigureAwait(false);

        return await resultStore.RecordResultAsync(
            command.Scope, command.PlanVersion, resolved.ScenarioId, resolved.Status, resolved.Evidence, resolved.ReasonCode,
            resolved.ObservedAtUtc, actor, role, command.Correlation, now, cancellationToken).ConfigureAwait(false);
    }
}
