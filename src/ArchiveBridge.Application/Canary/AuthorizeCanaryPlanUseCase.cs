using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Comando de autorização de UM plano de canário (AB-I8-004). O caller fornece SOMENTE o escopo — o
/// Production Readiness Review canônico e vigente é SEMPRE resolvido server-side por este use case a partir
/// de <see cref="IProductionReadinessReviewStore"/> (mesmo princípio de AB-I8-002 blocker 1: nenhum
/// identificador/fingerprint arbitrário do caller é aceito como o review vinculado). Nunca carrega ator/
/// papel: identidade e papéis efetivos são sempre resolvidos server-side pelo use case a partir de
/// <see cref="IAuthenticatedActorAccessor"/>.
/// </summary>
public sealed record AuthorizeCanaryPlanCommand(TenantScope Scope, CorrelationId Correlation);

/// <summary>
/// Autoriza (ou converge idempotentemente para) a versão VIGENTE do plano de canário de um tenant/projeto
/// (AB-I8-004, runbook §48, escopo obrigatório itens 1-2). Resolve o Production Readiness Review canônico e
/// vigente do escopo; SOMENTE <see cref="ProductionReadinessOutcome.ReadyForCanary"/> permite a transição —
/// qualquer outro desfecho (ou nenhum review ainda composto) bloqueia ANTES de qualquer efeito externo, sem
/// criar plano algum. NUNCA inicia canário real, NUNCA marca projeto/wave concluído, NUNCA escreve em
/// Purview/EXO/Graph/EV/AzCopy/host real (STOP-THE-LINE).
/// </summary>
public sealed class AuthorizeCanaryPlanUseCase(
    IProductionReadinessReviewStore readinessStore,
    ICanaryPlanStore planStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="CanaryEntryGateBlockedException">Nenhum Production Readiness Review vigente, ou seu desfecho não é ReadyForCanary.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryPlan> ExecuteAsync(AuthorizeCanaryPlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // RBAC SEMPRE antes de qualquer acesso a dado de escopo — identidade/papéis vêm EXCLUSIVAMENTE de
        // IAuthenticatedActorAccessor, nunca do comando (mesmo princípio AB-I6-012).
        var authenticatedActor = actorAccessor.Current;
        var actor = CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = CanaryAuthorization.EnsureCanAuthorizePlan(authenticatedActor.Roles);

        var now = clock.UtcNow;

        // Gate de entrada (escopo obrigatório item 2): resolvido EXCLUSIVAMENTE server-side, a partir do
        // review canônico e vigente já composto pelo Passo anterior (AB-I8-001/002/003) — nunca aceito do
        // caller. Ausência de review OU qualquer desfecho diferente de ReadyForCanary bloqueia aqui, ANTES de
        // qualquer chamada ao ICanaryPlanStore — nenhum plano é criado.
        var readiness = await readinessStore.GetLatestAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        if (readiness is null)
        {
            throw new CanaryEntryGateBlockedException(
                "Um plano de canário não pode ser autorizado: nenhum Production Readiness Review foi composto " +
                "ainda para este tenant/projeto (fail-closed).");
        }

        if (readiness.Outcome != ProductionReadinessOutcome.ReadyForCanary)
        {
            throw new CanaryEntryGateBlockedException(
                $"Um plano de canário não pode ser autorizado: o Production Readiness Review vigente (versão " +
                $"{readiness.ReviewVersion}) está {readiness.Outcome}, não ReadyForCanary (fail-closed).");
        }

        // O build/digest/policy/capability fingerprint são SEMPRE herdados do review EXATO já revisado —
        // nunca fornecidos pelo chamador (mesmo princípio de AB-I8-002 blocker 1 aplicado ao plano de
        // canário: não existe fork de "build de canário" e "build de produção", escopo obrigatório item 5).
        return await planStore.AuthorizeAsync(
            command.Scope,
            readiness.ReviewVersion,
            readiness.ReviewFingerprint,
            readiness.Outcome,
            readiness.BuildCommitSha,
            readiness.BuildArtifactDigest,
            readiness.PolicyVersionFingerprint,
            readiness.CapabilityMatrixFingerprint,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
