using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Verifica explicitamente a integridade de UMA versão específica, já emitida, de um reconciliation
/// certificate (AB-I6-013 item 14: "verificável offline quanto à própria integridade e referências/
/// fingerprints, sem depender de chamada a Purview/EXO/EV durante a verificação do documento"). A
/// revalidação em si é inteiramente feita por <see cref="ReconciliationCertificate.Rehydrate"/> (fail-closed,
/// recusa devolver um certificate cujo <see cref="ReconciliationCertificate.CertificateHash"/> recomputado
/// diverge do persistido); este use case apenas orquestra a leitura e a auditoria do resultado (item 20).
/// Leitura pura, sem RBAC adicional além do já aplicado nas telas do portal.
/// </summary>
public sealed class VerifyReconciliationCertificateUseCase(IReconciliationCertificateStore certificates, IAuthenticatedActorAccessor actorAccessor, IClock clock)
{
    private readonly IReconciliationCertificateStore _certificates = certificates;
    private readonly IAuthenticatedActorAccessor _actorAccessor = actorAccessor;
    private readonly IClock _clock = clock;

    /// <summary><see langword="null"/> quando a versão referenciada é inexistente/fora de escopo (anti-IDOR).</summary>
    /// <exception cref="ReconciliationCertificateIntegrityViolationException">O certificate_hash persistido diverge do recomputado (registrado como evento auditável antes de propagar).</exception>
    public async Task<ReconciliationCertificate?> ExecuteAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int certificateVersion, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var authenticatedActor = _actorAccessor.Current;
        var auditRole = authenticatedActor.Roles.FirstOrDefault() ?? "unknown";

        ReconciliationCertificate? certificate;
        try
        {
            certificate = await _certificates.GetByVersionAsync(scope, wave, plannedJobName, certificateVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (ReconciliationCertificateIntegrityViolationException)
        {
            await _certificates.RecordAuditEventAsync(
                scope, wave, plannedJobName, certificateVersion, ReconciliationCertificateAuditEventType.IntegrityViolationDetected,
                authenticatedActor.ActorId, auditRole, false, "certificate_hash divergente na verificação explícita da versão referenciada.",
                correlation, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (certificate is not null)
        {
            await _certificates.RecordAuditEventAsync(
                scope, wave, plannedJobName, certificateVersion, ReconciliationCertificateAuditEventType.Verified,
                authenticatedActor.ActorId, auditRole, true, "certificate_hash revalidado com sucesso (verificação offline explícita).",
                correlation, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        return certificate;
    }
}
