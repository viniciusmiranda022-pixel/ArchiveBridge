using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de resolução do cenário CANARY.PST_SIZE_BOUNDARY_COVERAGE (AB-I8-006) — escopado aos DOIS
/// artefatos PST candidatos (pequeno + boundary de 18 GB) cujas <see cref="PstInspectionRecord"/> canônicas
/// devem ser observadas.
/// </summary>
public sealed record ResolveCanaryPstSizeBoundaryEvidenceCommand(
    TenantScope Scope,
    int PlanVersion,
    ArtifactId SmallArtifact,
    Sha256Hash SmallExpectedHash,
    ArtifactId BoundaryArtifact,
    Sha256Hash BoundaryExpectedHash,
    CorrelationId Correlation);

/// <summary>
/// Resolve e persiste o cenário <c>CANARY.PST_SIZE_BOUNDARY_COVERAGE</c> a partir do
/// <c>ObservedSizeBytes</c> REAL de duas <see cref="PstInspectionRecord"/> canônicas JÁ PERSISTIDAS (Slice
/// 4B/<see cref="IPstInspectionStore"/>), via <see cref="CanaryScenarioEvidenceResolvers"/>. Escopado a UMA
/// versão específica e VIGENTE do plano. NUNCA marca canário/go-live/projeto concluído, NUNCA reexecuta
/// inspeção real (STOP-THE-LINE) — apenas LÊ evidência já persistida.
/// </summary>
public sealed class ResolveCanaryPstSizeBoundaryEvidenceUseCase(
    IPstInspectionStore inspectionStore,
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(ResolveCanaryPstSizeBoundaryEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = await CanaryScenarioEvidenceResolvers.ResolvePstSizeBoundaryCoverageAsync(
            inspectionStore, command.Scope, command.SmallArtifact, command.SmallExpectedHash, command.BoundaryArtifact,
            command.BoundaryExpectedHash, now, cancellationToken).ConfigureAwait(false);

        return await resultStore.RecordResultAsync(
            command.Scope, command.PlanVersion, resolved.ScenarioId, resolved.Status, resolved.Evidence, resolved.ReasonCode,
            resolved.ObservedAtUtc, actor, role, command.Correlation, now, cancellationToken).ConfigureAwait(false);
    }
}
