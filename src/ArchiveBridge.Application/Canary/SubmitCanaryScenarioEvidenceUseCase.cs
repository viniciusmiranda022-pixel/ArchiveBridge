using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de submissão de UMA atestação de operador para um cenário
/// <see cref="CanaryScenarioEvidenceSource.OperatorAttested"/> do catálogo (AB-I8-004, escopo obrigatório
/// item 6). O caller fornece SOMENTE o cenário/status/evidência/versão do plano — identidade e papéis
/// efetivos são SEMPRE resolvidos server-side pelo use case a partir de
/// <see cref="IAuthenticatedActorAccessor"/> (mesmo princípio AB-I6-012).
/// </summary>
public sealed record SubmitCanaryScenarioEvidenceCommand(
    TenantScope Scope,
    int PlanVersion,
    CanaryScenarioId ScenarioId,
    CanaryScenarioStatus Status,
    string EvidenceDescription,
    string ReasonCode,
    DateTimeOffset ObservedAtUtc,
    CorrelationId Correlation);

/// <summary>
/// Submete (ou converge idempotentemente para) uma atestação de operador de UM cenário
/// <see cref="CanaryScenarioEvidenceSource.OperatorAttested"/> do catálogo, escopada a UMA versão específica
/// e VIGENTE do plano de canário. RECUSA fail-closed, ANTES de qualquer acesso a dado de escopo, tanto ator
/// anônimo/não autorizado quanto tentativa de atestar um cenário <see cref="CanaryScenarioEvidenceSource.SystemDerived"/>
/// ou o gate de aprovação (<see cref="CanaryScenarioEvidenceSource.ApprovalDecision"/>) — bloqueio
/// estrutural. RECUSA também submissões contra uma versão do plano que já não é a vigente (drift, escopo
/// obrigatório item 5). NUNCA marca canário/go-live/projeto concluído (STOP-THE-LINE).
/// </summary>
public sealed class SubmitCanaryScenarioEvidenceUseCase(
    ICanaryScenarioResultStore resultStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryScenarioNotAttestableException"><see cref="SubmitCanaryScenarioEvidenceCommand.ScenarioId"/> é SystemDerived, é o gate de aprovação, ou é desconhecido.</exception>
    /// <exception cref="CanaryPlanSupersededException">A versão do plano informada já não é a vigente do escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryScenarioResult> ExecuteAsync(SubmitCanaryScenarioEvidenceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanSubmitEvidence(authenticatedActor.Roles);

        // Bloqueio estrutural: mesmo um ator autorizado a submeter evidência nunca pode "atestar" um cenário
        // SystemDerived ou o gate de aprovação — checagem redundante com CanaryScenarioResult (defesa em
        // profundidade), lançada ANTES de computar o fingerprint da evidência.
        CanaryScenarioCatalog.RequireOperatorAttestable(command.ScenarioId);

        var now = clock.UtcNow;
        var evidenceFingerprint = DeterministicHash.Compute(
            ["archivebridge.canary.operator-evidence.v1", command.ScenarioId.Value, command.EvidenceDescription]);
        var evidence = CanaryEvidenceReference.OperatorAttested(evidenceFingerprint, command.EvidenceDescription);

        return await resultStore.RecordResultAsync(
            command.Scope,
            command.PlanVersion,
            command.ScenarioId,
            command.Status,
            evidence,
            command.ReasonCode,
            command.ObservedAtUtc,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
