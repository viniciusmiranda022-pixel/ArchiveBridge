using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.GoLive;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

namespace ArchiveBridge.Application.GoLive;

/// <summary>
/// Comando de autorização de go-live/primeira onda real (AB-I8-010). O caller fornece SOMENTE o escopo — o
/// plano de canário canônico e vigente, o Production Readiness Review vigente e todos os controles
/// operacionais/M365 são SEMPRE resolvidos server-side por este use case, nunca aceitos do caller (mesmo
/// princípio de AB-I8-002 blocker 1 aplicado à decisão de go-live: nenhum identificador/fingerprint arbitrário
/// do caller é aceito como evidência canônica; nenhum campo de request pode afirmar que readiness/canário
/// passaram).
/// </summary>
public sealed record AuthorizeGoLiveCommand(TenantScope Scope, CorrelationId Correlation);

/// <summary>
/// Autoriza (ou converge idempotentemente para) a versão VIGENTE da decisão de go-live de um tenant/projeto
/// (AB-I8-010, runbook §48 item 185, escopo obrigatório itens 1-5). Resolve o plano de canário canônico e
/// vigente do escopo — ausência bloqueia ANTES de qualquer efeito, sem criar decisão alguma (nenhuma
/// dependência para vincular/julgar). Uma vez vinculado, sempre PERSISTE uma decisão (mesmo padrão de
/// <c>ComposeProductionReadinessReviewUseCase</c>: <see cref="GoLiveOutcome.Blocked"/> é um desfecho
/// auditável, não uma exceção) com o canário resolvido, o Production Readiness Review vigente comparado
/// contra o vinculado pelo canário (drift bloqueia — escopo obrigatório item 3), e os controles
/// operacionais/M365 revalidados FRESCOS (escopo obrigatório item 4). NUNCA inicia efeito real em
/// Purview/EXO/Graph/EV/AzCopy/host/tenant M365, NUNCA marca migração/projeto/wave <c>Completed</c>
/// (STOP-THE-LINE).
/// </summary>
public sealed class AuthorizeGoLiveUseCase(
    ICanaryPlanStore planStore,
    ICanaryScenarioResultStore resultStore,
    IProductionReadinessReviewStore readinessStore,
    IRecoveryReadinessStore recoveryReadinessStore,
    IMailboxPrecheckStore mailboxPrecheckStore,
    IMappingValidationStore mappingValidationStore,
    IPurviewUploadAttemptStore uploadAttemptStore,
    AzCopyHomologationCatalog homologatedBinaries,
    IReadinessControlAttestationStore attestationStore,
    IGoLiveAuthorizationStore authorizationStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="GoLiveAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="GoLiveEntryGateBlockedException">Nenhum plano de canário existe ainda para este escopo.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<GoLiveAuthorizationDecision> ExecuteAsync(AuthorizeGoLiveCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // RBAC SEMPRE antes de qualquer acesso a dado de escopo — identidade/papéis vêm EXCLUSIVAMENTE de
        // IAuthenticatedActorAccessor, nunca do comando (mesmo princípio AB-I6-012).
        var authenticatedActor = actorAccessor.Current;
        var actor = GoLiveAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = GoLiveAuthorization.EnsureCanAuthorize(authenticatedActor.Roles);

        var now = clock.UtcNow;

        // Gate de entrada estrutural (escopo obrigatório item 2): sem NENHUM plano de canário para o escopo
        // não há dependência alguma para vincular/julgar — nenhuma decisão é criada.
        var plan = await planStore.GetLatestAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            throw new GoLiveEntryGateBlockedException(
                "Go-live não pode ser avaliado: nenhum plano de canário foi autorizado ainda para este tenant/projeto (fail-closed).");
        }

        // O desfecho do canário é SEMPRE resolvido server-side a partir dos resultados de cenário já
        // persistidos — nunca aceito do caller.
        var canaryResults = await resultStore.GetAllLatestForPlanAsync(command.Scope, plan.PlanVersion, cancellationToken).ConfigureAwait(false);
        var canaryEvaluation = CanaryGateEvaluator.Evaluate(canaryResults, now);

        // O Production Readiness Review VIGENTE é resolvido FRESCO agora (nunca o cacheado no instante do
        // canário) — comparado, dentro do avaliador puro, contra o vinculado pelo plano de canário para
        // detectar drift (escopo obrigatório item 3).
        var currentReadiness = await readinessStore.GetLatestAsync(command.Scope, cancellationToken).ConfigureAwait(false);

        // Controles operacionais/M365 revalidados FRESCOS agora (escopo obrigatório item 4) — nunca
        // reaproveitados do snapshot do Production Readiness Review original.
        var operationalResolvedResults = await GoLiveOperationalEvidenceResolvers.ResolveAllAsync(
            recoveryReadinessStore, mailboxPrecheckStore, mappingValidationStore, uploadAttemptStore, homologatedBinaries,
            attestationStore, command.Scope, now, cancellationToken).ConfigureAwait(false);

        // Build/commit/digest/policy/capability são SEMPRE herdados EXATAMENTE do canário vinculado — nunca
        // fornecidos pelo chamador (escopo obrigatório item 3: same-build/same-policy promotion invariant).
        return await authorizationStore.AuthorizeAsync(
            command.Scope,
            plan.PlanId,
            plan.PlanVersion,
            plan.PlanFingerprint,
            plan.ReadinessReviewVersion,
            plan.ReadinessReviewFingerprint,
            plan.BuildCommitSha,
            plan.BuildArtifactDigest,
            plan.PolicyVersionFingerprint,
            plan.CapabilityMatrixFingerprint,
            canaryEvaluation.Outcome,
            currentReadiness?.ReviewVersion,
            currentReadiness?.ReviewFingerprint,
            operationalResolvedResults,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
