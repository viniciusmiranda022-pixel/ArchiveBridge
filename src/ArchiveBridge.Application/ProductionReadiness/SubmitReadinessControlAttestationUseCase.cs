using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Application.ProductionReadiness;

/// <summary>
/// Comando de submissão de UMA atestação manual (AB-I8-001 escopo item 9). O caller fornece SOMENTE o
/// controle/status/evidência — identidade e papéis efetivos são SEMPRE resolvidos server-side pelo use case
/// a partir de <see cref="IAuthenticatedActorAccessor"/> (mesmo princípio AB-I6-012 de
/// <c>DisposeReconciliationExceptionCommand</c>/<c>IssueReconciliationCertificateCommand</c>: nenhum
/// ator/papel fornecido pelo payload é confiável).
/// </summary>
public sealed record SubmitReadinessControlAttestationCommand(
    TenantScope Scope,
    ReadinessControlId ControlId,
    ReadinessControlStatus Status,
    string EvidenceDescription,
    string ReasonCode,
    CorrelationId Correlation);

/// <summary>
/// Submete (ou converge idempotentemente para) uma atestação manual de UM controle
/// <see cref="ReadinessControlEvidenceSource.Attested"/> do catálogo. RECUSA fail-closed, ANTES de
/// qualquer acesso a dado de escopo, tanto ator anônimo/não autorizado quanto tentativa de atestar um
/// controle <see cref="ReadinessControlEvidenceSource.SystemDerived"/> (bloqueio estrutural: pen-test/RTO/
/// RPO/SBOM/WDAC/incident-response/hashes-manifests-lineage/backup-restore/target-root-policy/import-limits
/// nunca podem ser "aprovados" por alegação humana). NUNCA marca canário/go-live/projeto concluído
/// (STOP-THE-LINE).
/// </summary>
public sealed class SubmitReadinessControlAttestationUseCase(
    IReadinessControlAttestationStore attestations,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    private readonly IReadinessControlAttestationStore _attestations = attestations;
    private readonly IClock _clock = clock;
    private readonly IAuthenticatedActorAccessor _actorAccessor = actorAccessor;

    /// <exception cref="ProductionReadinessAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="ProductionReadinessAttestationNotAllowedException"><see cref="SubmitReadinessControlAttestationCommand.ControlId"/> é SystemDerived ou desconhecido.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<ReadinessControlAttestation> ExecuteAsync(SubmitReadinessControlAttestationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // RBAC SEMPRE antes de qualquer acesso a dado de escopo (mesmo princípio anti-enumeração de
        // IssueReconciliationCertificateUseCase) — identidade/papéis vêm EXCLUSIVAMENTE de
        // IAuthenticatedActorAccessor, nunca do comando.
        var authenticatedActor = _actorAccessor.Current;
        var actor = ProductionReadinessAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = ProductionReadinessAuthorization.EnsureCanWrite(authenticatedActor.Roles);

        // Bloqueio estrutural: mesmo um ator autorizado a atestar nunca pode aprovar um controle
        // SystemDerived — a checagem de catálogo acontece de novo aqui (redundante com
        // ReadinessControlAttestation.Create) para que a exceção específica seja lançada ANTES de computar
        // o fingerprint da evidência.
        ReadinessControlAttestation.RequireAttestable(command.ControlId);

        var now = _clock.UtcNow;
        var evidenceFingerprint = DeterministicHash.Compute(
            ["archivebridge.production-readiness.manual-evidence.v1", command.ControlId.Value, command.EvidenceDescription]);
        var evidence = ReadinessEvidenceReference.Attested(evidenceFingerprint, command.EvidenceDescription);

        return await _attestations.RecordAttestationAsync(
            command.Scope,
            command.ControlId,
            command.Status,
            evidence,
            command.ReasonCode,
            actor,
            role,
            command.Correlation,
            now,
            cancellationToken).ConfigureAwait(false);
    }
}
