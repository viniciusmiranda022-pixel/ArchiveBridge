using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Domain.Canary;

namespace ArchiveBridge.Application.Canary;

/// <summary>Consulta o plano de canário vigente de um escopo — <see langword="null"/> quando nenhum plano ainda foi autorizado (nunca revela existência cross-tenant, ver <see cref="TenantScope"/>/RLS).</summary>
public sealed record GetCanaryPlanReportQuery(TenantScope Scope);

/// <summary>
/// Lê o plano VIGENTE (não recompõe cenários — cada resolução/atestação já foi persistida por
/// <see cref="ResolveCanarySystemEvidenceUseCase"/>/<see cref="ResolveCanaryReconciliationEvidenceUseCase"/>/
/// <see cref="SubmitCanaryScenarioEvidenceUseCase"/>/<see cref="ApproveCanaryFirstWaveUseCase"/>) e projeta um
/// relatório SANITIZADO (AB-I8-004), incluindo se o build sob canário ainda é o candidato promovível
/// (nenhum drift do Production Readiness Review desde a autorização do plano — escopo obrigatório item 5).
/// RBAC de leitura permite qualquer papel reconhecido do portal — nunca ator anônimo.
/// </summary>
public sealed class GetCanaryPlanReportUseCase(
    ICanaryPlanStore planStore,
    ICanaryScenarioResultStore resultStore,
    IProductionReadinessReviewStore readinessStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="CanaryAuthorizationException">Ator anônimo ou nenhum papel efetivo reconhecido.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<CanaryPlanReportView?> ExecuteAsync(GetCanaryPlanReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var authenticatedActor = actorAccessor.Current;
        CanaryAuthorization.RequireActor(authenticatedActor.ActorId);
        CanaryAuthorization.EnsureCanRead(authenticatedActor.Roles);

        var plan = await planStore.GetLatestAsync(query.Scope, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return null;
        }

        var resolvedResults = await resultStore.GetAllLatestForPlanAsync(query.Scope, plan.PlanVersion, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var evaluation = CanaryGateEvaluator.Evaluate(resolvedResults, now);

        // Drift check (escopo obrigatório item 5): o Production Readiness Review canônico VIGENTE pode ter
        // avançado (nova revisão, build/commit/policy/capability diferentes) desde que este plano foi
        // autorizado. ReviewFingerprint já cobre deterministicamente build/commit/digest/policy/capability +
        // TODOS os controles resolvidos (ProductionReadinessReviewSnapshot.ComputeReviewFingerprint) — uma
        // divergência ali É, por construção, uma divergência em qualquer uma dessas dependências.
        var currentReadiness = await readinessStore.GetLatestAsync(query.Scope, cancellationToken).ConfigureAwait(false);
        var readinessHasDrifted = currentReadiness is null
            || currentReadiness.ReviewVersion != plan.ReadinessReviewVersion
            || !string.Equals(currentReadiness.ReviewFingerprint.Value, plan.ReadinessReviewFingerprint.Value, StringComparison.Ordinal);

        var isPromotable = evaluation.Outcome == CanaryOutcome.CanaryPassed && !readinessHasDrifted;

        return CanaryPlanReportFormatter.ToReportView(plan, evaluation, isPromotable, readinessHasDrifted);
    }
}
