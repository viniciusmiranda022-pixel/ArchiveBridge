using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.MigrationCompletion;

/// <summary>
/// Comando de composição de UMA nova avaliação de encerramento de migração (AB-I8-010, runbook §49). O caller
/// identifica SOMENTE o escopo e a onda/plano de import job cuja evidência técnica ancora os dois critérios
/// <c>SystemDerived</c> (reconciliação/resultados do provider — este repositório não expõe uma consulta "todas
/// as ondas de um projeto", ver <see cref="MigrationCompletionAssessment"/>); nenhum campo do comando pode
/// afirmar que qualquer critério passou.
/// </summary>
public sealed record ComposeMigrationCompletionAssessmentCommand(
    TenantScope Scope, WaveId AnchorWave, PurviewImportJobName AnchorPlannedJobName, CorrelationId Correlation);

/// <summary>
/// Compõe (ou converge idempotentemente para) a versão VIGENTE da avaliação de encerramento de migração de um
/// tenant/projeto (AB-I8-010, escopo obrigatório itens 7-8; classificação corrigida por AB-I8-011). Resolve
/// <c>COMPLETION.RECONCILIATION_CLOSED</c> a partir do reconciliation certificate canônico e vigente
/// (<see cref="IReconciliationCertificateStore"/>, I6) e <c>COMPLETION.PROVIDER_RESULTS_COLLECTED</c> a partir
/// do validation report/service result mais recente já importado (<see cref="IPurviewServiceResultReportStore"/>,
/// I6) — os dois únicos critérios <c>SystemDerived</c>. Resolve os quatro critérios <c>EvidenceDerived</c>
/// (disposition de fontes/parts, publicação WORM, ausência de credencial temporária) SEMPRE como
/// <c>NotMeasured</c> com um reason code específico e estável (AB-I8-011: nenhum store canônico suficiente
/// existe hoje neste repositório para nenhum deles — a resolução correta e fail-closed é permanecer bloqueante,
/// nunca um resolver parcial/heurístico e nunca uma atestação humana). Para os cinco critérios
/// <c>HumanApproval</c> restantes, aplica a atestação manual vigente (se houver) — ausente permanece
/// <c>NotMeasured</c> por default no avaliador (nunca fabricado). Delega a agregação PURA para
/// <see cref="MigrationCompletionAssessment.Compose"/>. NUNCA marca migração/projeto/wave <c>Completed</c>,
/// NUNCA executa decommission/exclusão destrutiva, NUNCA escreve em Purview/EXO/Graph/EV real (STOP-THE-LINE).
/// </summary>
public sealed class ComposeMigrationCompletionAssessmentUseCase(
    IReconciliationCertificateStore reconciliationStore,
    IPurviewServiceResultReportStore serviceResultStore,
    IMigrationCompletionCriterionAttestationStore attestationStore,
    IMigrationCompletionAssessmentStore assessmentStore,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    private static readonly MigrationCompletionCriterionId ReconciliationClosedId = new("COMPLETION.RECONCILIATION_CLOSED");
    private static readonly MigrationCompletionCriterionId ProviderResultsCollectedId = new("COMPLETION.PROVIDER_RESULTS_COLLECTED");

    // AB-I8-011: os quatro critérios EvidenceDerived — verdade técnica/objetiva, mas SEM store canônico
    // suficiente neste repositório hoje. Cada um resolve SEMPRE para NotMeasured com um reason code específico
    // (nunca o genérico "CRITERION_EVIDENCE_MISSING" sintetizado pelo avaliador para uma chave simplesmente
    // ausente) — auditável, estável, e nunca satisfeito por atestação (bloqueio estrutural em
    // MigrationCompletionCriterionAttestation.RequireAttestable). Substituir CADA UM destes por um resolver real
    // é trabalho de um slice futuro, quando o store canônico correspondente existir — nunca por enfraquecer
    // esta classificação de volta para HumanApproval/Attested.
    private static readonly (MigrationCompletionCriterionId Id, string ReasonCode)[] NotYetVerifiableEvidenceDerivedCriteria =
    [
        (new MigrationCompletionCriterionId("COMPLETION.SOURCE_DISPOSITION_COMPLETE"), "NO_CANONICAL_SOURCE_DISPOSITION_STORE"),
        (new MigrationCompletionCriterionId("COMPLETION.PARTS_DISPOSITION_COMPLETE"), "NO_CANONICAL_PARTS_DISPOSITION_STORE"),
        (new MigrationCompletionCriterionId("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM"), "NO_CANONICAL_EVIDENCE_PACKAGE_WORM_PUBLICATION_STORE"),
        (new MigrationCompletionCriterionId("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL"), "NO_CANONICAL_TEMPORARY_CREDENTIAL_REGISTRY"),
    ];

    /// <exception cref="MigrationCompletionAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    public async Task<MigrationCompletionAssessment> ExecuteAsync(
        ComposeMigrationCompletionAssessmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authenticatedActor = actorAccessor.Current;
        var actor = MigrationCompletionAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = MigrationCompletionAuthorization.EnsureCanWrite(authenticatedActor.Roles);

        var now = clock.UtcNow;
        var resolved = new Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult>();

        void Add(MigrationCompletionCriterionResult result) => resolved[result.CriterionId] = result;

        var certificate = await reconciliationStore.GetLatestAsync(command.Scope, command.AnchorWave, command.AnchorPlannedJobName, cancellationToken)
            .ConfigureAwait(false);
        Add(ResolveReconciliationClosed(certificate, now));

        var report = await serviceResultStore.GetLatestAsync(command.Scope, command.AnchorWave, command.AnchorPlannedJobName, cancellationToken)
            .ConfigureAwait(false);
        Add(ResolveProviderResultsCollected(report, now));

        // EvidenceDerived — sempre NotMeasured (AB-I8-011): nenhum store canônico suficiente existe hoje para
        // nenhum destes quatro critérios; nunca resolvido por atestação (ver RequireAttestable) nem por
        // heurística parcial.
        foreach (var (criterionId, reasonCode) in NotYetVerifiableEvidenceDerivedCriteria)
        {
            Add(MigrationCompletionCriterionResult.NotMeasured(criterionId, reasonCode, now));
        }

        // HumanApproval — atestação manual VIGENTE de cada critério já atestado; ausente permanece NotMeasured
        // por default no avaliador (nunca fabricado aqui).
        var attestations = await attestationStore.GetLatestForAllAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        foreach (var attestation in attestations)
        {
            if (!MigrationCompletionCriterionCatalog.IsKnown(attestation.CriterionId))
            {
                continue;
            }

            var definition = MigrationCompletionCriterionCatalog.Definition(attestation.CriterionId);
            if (definition.EvidenceSource != MigrationCompletionCriterionEvidenceSource.HumanApproval)
            {
                continue;
            }

            Add(MigrationCompletionCriterionResult.Create(
                attestation.CriterionId, attestation.Status, attestation.Evidence, attestation.ReasonCode, attestation.SubmittedAtUtc));
        }

        return await assessmentStore.RecordAssessmentAsync(
            command.Scope, command.AnchorWave, command.AnchorPlannedJobName, resolved, actor, role, command.Correlation, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private static MigrationCompletionCriterionResult ResolveReconciliationClosed(ReconciliationCertificate? certificate, DateTimeOffset now)
    {
        if (certificate is null)
        {
            return MigrationCompletionCriterionResult.NotMeasured(ReconciliationClosedId, "RECONCILIATION_NOT_CERTIFIED", now);
        }

        var evidence = ReadinessEvidenceReference.SystemDerived(
            certificate.EvaluationFingerprint, $"reconciliation-certificate:v{certificate.CertificateVersion}");

        if (certificate.DuplicateRiskDetected
            || certificate.Result is ReconciliationOutcome.Fail or ReconciliationOutcome.Inconclusive or ReconciliationOutcome.DuplicateRisk)
        {
            return MigrationCompletionCriterionResult.Create(
                ReconciliationClosedId, ReadinessControlStatus.Fail, evidence, "RECONCILIATION_NOT_CLOSED", certificate.GeneratedAtUtc);
        }

        if (!certificate.Completeness.IsComplete)
        {
            return MigrationCompletionCriterionResult.Create(
                ReconciliationClosedId, ReadinessControlStatus.Blocked, evidence, "RECONCILIATION_EVIDENCE_INCOMPLETE", certificate.GeneratedAtUtc);
        }

        // Result é Pass ou PassWithExplainedExceptions, evidência completa, nenhum duplicate risk.
        return MigrationCompletionCriterionResult.Create(
            ReconciliationClosedId, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, certificate.GeneratedAtUtc);
    }

    private static MigrationCompletionCriterionResult ResolveProviderResultsCollected(PurviewServiceResultReportEvidence? report, DateTimeOffset now)
    {
        if (report is null)
        {
            return MigrationCompletionCriterionResult.NotMeasured(ProviderResultsCollectedId, "PROVIDER_RESULTS_NOT_COLLECTED", now);
        }

        var evidence = ReadinessEvidenceReference.SystemDerived(
            report.EvidenceHash, $"purview-service-result-report:v{report.ReportVersion}");
        return MigrationCompletionCriterionResult.Create(
            ProviderResultsCollectedId, ReadinessControlStatus.Pass, evidence, reasonCode: string.Empty, report.CreatedAtUtc);
    }
}
