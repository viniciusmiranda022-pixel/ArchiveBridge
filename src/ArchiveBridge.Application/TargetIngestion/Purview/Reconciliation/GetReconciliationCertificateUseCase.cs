using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// O certificate VIGENTE de uma wave/plano junto de <see cref="IsSuperseded"/> (item 15/18 do work order): um
/// certificate cuja avaliação/dispositions de origem já não são mais as vigentes permanece histórico
/// (nunca apagado nem reescrito), mas nunca deve continuar sendo apresentado como certificate ATUAL sem essa
/// marca explícita — a marca é sempre COMPUTADA na leitura, nunca persistida no próprio certificate.
/// </summary>
public sealed record ReconciliationCertificateView(ReconciliationCertificate Certificate, bool IsSuperseded);

/// <summary>
/// Compõe a leitura do certificate VIGENTE de uma wave/plano (AB-I6-013), revalidando sua integridade
/// tamper-evident (item 12/14, fail-closed via <see cref="ReconciliationCertificate.Rehydrate"/>) e
/// calculando <see cref="ReconciliationCertificateView.IsSuperseded"/> ao comparar a avaliação/dispositions
/// que o certificate certificou contra a avaliação/dispositions REALMENTE vigentes agora — sem nunca sondar
/// Purview/EXO/Graph/EV (item 14: verificável offline). Leitura pura, sem RBAC adicional além do já
/// aplicado nas telas do portal (mesmo padrão de <see cref="GetReconciliationExceptionBacklogUseCase"/>) —
/// preserva tenant/project/wave anti-IDOR via <see cref="TenantScope"/> (item 18). Registra os eventos
/// auditáveis <see cref="ReconciliationCertificateAuditEventType.Verified"/>/<see cref="ReconciliationCertificateAuditEventType.Superseded"/>/
/// <see cref="ReconciliationCertificateAuditEventType.IntegrityViolationDetected"/> (item 20).
/// </summary>
public sealed class GetReconciliationCertificateUseCase(
    IReconciliationCertificateStore certificates,
    IReconciliationAssessmentStore assessments,
    IReconciliationExceptionDispositionStore dispositions,
    IAuthenticatedActorAccessor actorAccessor,
    IClock clock)
{
    private readonly IReconciliationCertificateStore _certificates = certificates;
    private readonly IReconciliationAssessmentStore _assessments = assessments;
    private readonly IReconciliationExceptionDispositionStore _dispositions = dispositions;
    private readonly IAuthenticatedActorAccessor _actorAccessor = actorAccessor;
    private readonly IClock _clock = clock;

    /// <summary><see langword="null"/> quando a onda/plano é inexistente/fora de escopo, ou nenhum certificate ainda foi emitido.</summary>
    /// <exception cref="ReconciliationCertificateIntegrityViolationException">O certificate_hash persistido diverge do recomputado (registrado como evento auditável antes de propagar).</exception>
    public async Task<ReconciliationCertificateView?> ExecuteAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var authenticatedActor = _actorAccessor.Current;
        var auditRole = authenticatedActor.Roles.FirstOrDefault() ?? "unknown";

        ReconciliationCertificate? certificate;
        try
        {
            certificate = await _certificates.GetLatestAsync(scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        }
        catch (ReconciliationCertificateIntegrityViolationException)
        {
            await _certificates.RecordAuditEventAsync(
                scope, wave, plannedJobName, null, ReconciliationCertificateAuditEventType.IntegrityViolationDetected,
                authenticatedActor.ActorId, auditRole, false, "certificate_hash divergente na leitura do certificate vigente.",
                correlation, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (certificate is null)
        {
            return null;
        }

        var isSuperseded = await IsEvidenceStaleAsync(scope, wave, plannedJobName, certificate, cancellationToken).ConfigureAwait(false);

        await _certificates.RecordAuditEventAsync(
            scope,
            wave,
            plannedJobName,
            certificate.CertificateVersion,
            isSuperseded ? ReconciliationCertificateAuditEventType.Superseded : ReconciliationCertificateAuditEventType.Verified,
            authenticatedActor.ActorId,
            auditRole,
            true,
            isSuperseded
                ? "A evidência canônica (avaliação e/ou dispositions) avançou desde a emissão deste certificate."
                : "Integridade revalidada com sucesso; evidência canônica ainda corresponde ao certificate vigente.",
            correlation,
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);

        return new ReconciliationCertificateView(certificate, isSuperseded);
    }

    private async Task<bool> IsEvidenceStaleAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, ReconciliationCertificate certificate, CancellationToken cancellationToken)
    {
        var latestAssessment = await _assessments.GetLatestAsync(scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (latestAssessment is null || latestAssessment.AssessmentVersion != certificate.AssessmentVersion)
        {
            return true;
        }

        var pstItems = await _assessments.GetPstItemsAsync(scope, wave, plannedJobName, certificate.AssessmentVersion, cancellationToken).ConfigureAwait(false);
        var archiveItems = await _assessments.GetArchiveItemsAsync(scope, wave, plannedJobName, certificate.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        var currentDecisions = await _dispositions
            .GetCurrentDecisionsForAssessmentAsync(scope, wave, plannedJobName, certificate.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);

        var backlog = ReconciliationExceptionWaveBacklog.From(certificate.AssessmentVersion, pstItems, archiveItems, currentDecisions);
        var deviations = ReconciliationCertificateRules.BuildDeviationSummary(backlog);
        var freshDeviationsSha256 = ReconciliationCertificateDeviationsHash.Compute(deviations);

        return !string.Equals(freshDeviationsSha256.Value, certificate.DeviationsSha256.Value, StringComparison.Ordinal);
    }
}
