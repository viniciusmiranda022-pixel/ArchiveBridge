using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Application.Canary;

/// <summary>
/// Projeta um <see cref="CanaryPlan"/> + <see cref="CanaryEvaluation"/> em um <see cref="CanaryPlanReportView"/>
/// SANITIZADO (AB-I8-004). Todo texto livre já passou pelo guarda fail-closed de
/// <see cref="SecretRedactor.ContainsSuspectedSecret"/> na escrita (no domínio); este formatter aplica
/// <see cref="SecretRedactor.Redact"/> como camada extra de defesa em profundidade antes de expor qualquer
/// texto num relatório (nunca a única defesa).
/// </summary>
public static class CanaryPlanReportFormatter
{
    private const string RedactionScope = "archivebridge.canary.report";

    /// <summary>Converte o plano + a agregação já resolvida em um relatório sanitizado explicando precisamente por que o canário está ou não CanaryPassed.</summary>
    public static CanaryPlanReportView ToReportView(
        CanaryPlan plan, CanaryEvaluation evaluation, bool isPromotable, bool readinessHasDrifted)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evaluation);

        var scenarios = evaluation.ScenarioResults
            .Select(result =>
            {
                var definition = CanaryScenarioCatalog.Definition(result.ScenarioId);
                return new CanaryScenarioView(
                    result.ScenarioId.Value,
                    SecretRedactor.Redact(definition.Description, RedactionScope),
                    definition.EvidenceSource,
                    result.Status,
                    SecretRedactor.Redact(result.Evidence.Locator, RedactionScope),
                    SecretRedactor.Redact(result.ReasonCode, RedactionScope),
                    result.ObservedAtUtc);
            })
            .ToList();

        var blockerSummaries = evaluation.Blockers
            .Select(blocker =>
            {
                var definition = CanaryScenarioCatalog.Definition(blocker.ScenarioId);
                var reason = string.IsNullOrEmpty(blocker.ReasonCode) ? blocker.Status.ToString() : blocker.ReasonCode;
                return SecretRedactor.Redact($"{blocker.ScenarioId.Value} ({definition.Description}): {blocker.Status} — {reason}", RedactionScope);
            })
            .ToList();

        return new CanaryPlanReportView(
            plan.PlanVersion, plan.BuildCommitSha, evaluation.Outcome, isPromotable, readinessHasDrifted, scenarios,
            blockerSummaries, plan.AuthorizedAtUtc);
    }
}
