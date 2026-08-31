using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de resolução do cenário CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT (AB-I8-006) — escopado à wave
/// específica do canário controlado cujo pedido de upload Purview deve ser observado, diferente dos demais
/// cenários SystemDerived não escopados a onda.
/// </summary>
public sealed record ResolveCanaryReplayIdempotencyEvidenceCommand(TenantScope Scope, int PlanVersion, WaveId Wave, CorrelationId Correlation);

/// <summary>
/// Resolve e persiste o cenário <c>CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT</c> a partir da história REAL de
/// tentativas de upload Purview (<see cref="IPurviewUploadAttemptStore"/>) do pedido canônico da wave, via
/// <see cref="CanaryScenarioEvidenceResolvers"/>. Escopado a UMA versão específica e VIGENTE do plano. NUNCA
/// marca canário/go-live/projeto concluído, NUNCA escreve em Purview/EXO/Graph/EV/AzCopy/host real
/// (STOP-THE-LINE) — apenas LÊ evidência já persistida por AB-I5-009.
/// </summary>
public sealed class ResolveCanaryReplayIdempotencyEvidenceUseCase(
    IPurviewUploadRequestStore requestStore,
    IPurviewUploadAttemptStore attemptStore,
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(ResolveCanaryReplayIdempotencyEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = await CanaryScenarioEvidenceResolvers.ResolveReplaySameTargetRootIdempotentAsync(
            requestStore, attemptStore, command.Scope, command.Wave, now, cancellationToken).ConfigureAwait(false);

        return await resultStore.RecordResultAsync(
            command.Scope, command.PlanVersion, resolved.ScenarioId, resolved.Status, resolved.Evidence, resolved.ReasonCode,
            resolved.ObservedAtUtc, actor, role, command.Correlation, now, cancellationToken).ConfigureAwait(false);
    }
}
