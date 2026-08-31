using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de resolução do cenário CANARY.DIFFERENT_TARGET_ROOT_BLOCKS (AB-I8-006) — escopado à wave do
/// canário e ao root CANDIDATO diferente que o operador quer ver recusado; o resolver nunca aceita o
/// veredito do caller, apenas exercita o guard real de domínio contra o candidato informado.
/// </summary>
public sealed record ResolveCanaryTargetRootGuardEvidenceCommand(
    TenantScope Scope, int PlanVersion, WaveId Wave, TargetRootFolder AttemptedDifferentRoot, CorrelationId Correlation);

/// <summary>
/// Resolve e persiste o cenário <c>CANARY.DIFFERENT_TARGET_ROOT_BLOCKS</c> exercitando o MESMO guard de
/// domínio que protege produção (<see cref="MigrationWave.ChangeTargetRootFolder"/>) contra um root diferente
/// do atual, via <see cref="CanaryScenarioEvidenceResolvers"/>. A wave carregada NUNCA é persistida de volta
/// — nenhuma mutação real ocorre, apenas a observação determinística de
/// <see cref="InvalidWaveTransitionException"/>. Escopado a UMA versão específica e VIGENTE do plano. NUNCA
/// marca canário/go-live/projeto concluído (STOP-THE-LINE).
/// </summary>
public sealed class ResolveCanaryTargetRootGuardEvidenceUseCase(
    IWaveStore waveStore,
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(ResolveCanaryTargetRootGuardEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = await CanaryScenarioEvidenceResolvers.ResolveDifferentTargetRootBlocksAsync(
            waveStore, command.Scope, command.Wave, command.AttemptedDifferentRoot, now, cancellationToken).ConfigureAwait(false);

        return await resultStore.RecordResultAsync(
            command.Scope, command.PlanVersion, resolved.ScenarioId, resolved.Status, resolved.Evidence, resolved.ReasonCode,
            resolved.ObservedAtUtc, actor, role, command.Correlation, now, cancellationToken).ConfigureAwait(false);
    }
}
