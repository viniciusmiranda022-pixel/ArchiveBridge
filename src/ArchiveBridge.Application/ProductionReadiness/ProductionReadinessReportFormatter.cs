using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>
/// Projeta um <see cref="ProductionReadinessReviewSnapshot"/> em um <see cref="ProductionReadinessReportView"/>
/// SANITIZADO (AB-I8-001 escopo item 8). Todo texto livre do snapshot já passou pelo guarda fail-closed de
/// <see cref="SecretRedactor.ContainsSuspectedSecret"/> na escrita (no domínio); este formatter aplica
/// <see cref="SecretRedactor.Redact"/> como camada extra de defesa em profundidade antes de expor qualquer
/// texto num relatório (nunca a única defesa).
/// </summary>
public static class ProductionReadinessReportFormatter
{
    /// <summary>Escopo usado para o redator de e-mail/UPN determinístico — nenhum e-mail real é esperado neste texto (só reforça o backstop).</summary>
    private const string RedactionScope = "archivebridge.production-readiness.report";

    /// <summary>Converte o snapshot em um relatório sanitizado explicando precisamente por que o sistema está ou não ReadyForCanary.</summary>
    public static ProductionReadinessReportView ToReportView(ProductionReadinessReviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var controls = snapshot.ControlResults
            .Select(result =>
            {
                var definition = ReadinessControlCatalog.Definition(result.ControlId);
                return new ProductionReadinessControlView(
                    result.ControlId.Value,
                    result.Group,
                    SecretRedactor.Redact(definition.Description, RedactionScope),
                    definition.EvidenceSource,
                    result.Status,
                    SecretRedactor.Redact(result.Evidence.Locator, RedactionScope),
                    SecretRedactor.Redact(result.ReasonCode, RedactionScope),
                    result.ObservedAtUtc);
            })
            .ToList();

        var blockerSummaries = snapshot.Blockers
            .Select(blocker =>
            {
                var definition = ReadinessControlCatalog.Definition(blocker.ControlId);
                var reason = string.IsNullOrEmpty(blocker.ReasonCode) ? blocker.Status.ToString() : blocker.ReasonCode;
                return SecretRedactor.Redact(
                    $"{blocker.Group}/{blocker.ControlId.Value} ({definition.Description}): {blocker.Status} — {reason}",
                    RedactionScope);
            })
            .ToList();

        return new ProductionReadinessReportView(
            snapshot.ReviewVersion, snapshot.BuildCommitSha, snapshot.Outcome, controls, blockerSummaries, snapshot.GeneratedAtUtc);
    }
}
