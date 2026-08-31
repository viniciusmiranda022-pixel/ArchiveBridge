using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Application.MigrationCompletion;

/// <summary>
/// Comando de submissão de UMA atestação manual de critério de encerramento (AB-I8-010, runbook §49). O
/// caller fornece SOMENTE o critério/status/evidência — identidade e papéis efetivos são SEMPRE resolvidos
/// server-side pelo use case (mesmo princípio AB-I6-012).
/// </summary>
public sealed record SubmitMigrationCompletionCriterionAttestationCommand(
    TenantScope Scope,
    MigrationCompletionCriterionId CriterionId,
    ReadinessControlStatus Status,
    string EvidenceDescription,
    string ReasonCode,
    CorrelationId Correlation);

/// <summary>
/// Submete (ou converge idempotentemente para) uma atestação manual de UM critério
/// <see cref="MigrationCompletionCriterionEvidenceSource.Attested"/> do catálogo do §49. RECUSA fail-closed,
/// ANTES de qualquer acesso a dado de escopo, tanto ator anônimo/não autorizado quanto tentativa de atestar um
/// critério <see cref="MigrationCompletionCriterionEvidenceSource.SystemDerived"/> (reconciliação/resultados
/// do provider nunca podem ser "aprovados" por alegação humana). Esta atestação é, ela própria, a evidência
/// auditável exigida pelo runbook §49 para os nove critérios sem store dedicado — inclui explicitamente
/// "cliente aprovou relatório final" (ausência nunca vira aprovação implícita, escopo obrigatório item 8) e
/// "janela de rollback/decommission definida" (registra APENAS a definição, nunca dispara ou representa
/// execução de decommission/exclusão destrutiva, escopo obrigatório item 9 — STOP-THE-LINE).
/// </summary>
public sealed class SubmitMigrationCompletionCriterionAttestationUseCase(
    IMigrationCompletionCriterionAttestationStore attestations,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    /// <exception cref="MigrationCompletionAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="MigrationCompletionAttestationNotAllowedException"><see cref="SubmitMigrationCompletionCriterionAttestationCommand.CriterionId"/> é SystemDerived ou desconhecido.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<MigrationCompletionCriterionAttestation> ExecuteAsync(
        SubmitMigrationCompletionCriterionAttestationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = MigrationCompletionAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = MigrationCompletionAuthorization.EnsureCanWrite(authenticatedActor.Roles);

        // Bloqueio estrutural: mesmo um ator autorizado a atestar nunca pode aprovar um critério SystemDerived
        // — a checagem de catálogo acontece de novo aqui (redundante com MigrationCompletionCriterionAttestation.Create)
        // para que a exceção específica seja lançada ANTES de computar o fingerprint da evidência.
        MigrationCompletionCriterionAttestation.RequireAttestable(command.CriterionId);

        var now = clock.UtcNow;
        var evidenceFingerprint = DeterministicHash.Compute(
            ["archivebridge.migration-completion.manual-evidence.v1", command.CriterionId.Value, command.EvidenceDescription]);
        var evidence = ReadinessEvidenceReference.Attested(evidenceFingerprint, command.EvidenceDescription);

        return await attestations.RecordAttestationAsync(
            command.Scope,
            command.CriterionId,
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
