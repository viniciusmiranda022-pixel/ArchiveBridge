using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de resolução do cenário CANARY.KNOWN_CORRUPTION_QUARANTINE (AB-I8-006) — escopado ao artefato PST
/// candidato a "corrupção conhecida" cuja <see cref="PstInspectionRecord"/> canônica deve ser observada.
/// </summary>
public sealed record ResolveCanaryPstCorruptionEvidenceCommand(
    TenantScope Scope, int PlanVersion, ArtifactId Artifact, Sha256Hash ExpectedHash, CorrelationId Correlation);

/// <summary>
/// Resolve e persiste o cenário <c>CANARY.KNOWN_CORRUPTION_QUARANTINE</c> a partir de uma
/// <see cref="PstInspectionRecord"/> canônica JÁ PERSISTIDA (Slice 4B/<see cref="IPstInspectionStore"/>), via
/// <see cref="CanaryScenarioEvidenceResolvers"/>. Escopado a UMA versão específica e VIGENTE do plano. NUNCA
/// marca canário/go-live/projeto concluído, NUNCA reexecuta inspeção real (STOP-THE-LINE) — apenas LÊ
/// evidência já persistida.
/// </summary>
public sealed class ResolveCanaryPstCorruptionEvidenceUseCase(
    IPstInspectionStore inspectionStore,
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(ResolveCanaryPstCorruptionEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = await CanaryScenarioEvidenceResolvers.ResolveKnownCorruptionQuarantineAsync(
            inspectionStore, command.Scope, command.Artifact, command.ExpectedHash, now, cancellationToken).ConfigureAwait(false);

        return await resultStore.RecordResultAsync(
            command.Scope, command.PlanVersion, resolved.ScenarioId, resolved.Status, resolved.Evidence, resolved.ReasonCode,
            resolved.ObservedAtUtc, actor, role, command.Correlation, now, cancellationToken).ConfigureAwait(false);
    }
}
