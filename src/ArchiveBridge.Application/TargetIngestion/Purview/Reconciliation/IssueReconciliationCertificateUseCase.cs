using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Comando de emissão do reconciliation certificate (AB-I6-013): o caller fornece SOMENTE identificadores
/// opacos da wave/plano (item 2) — toda evidência, avaliação e disposition usada no certificate é sempre
/// resolvida server-side no <see cref="TenantScope"/> autorizado. Nunca carrega ator/papel (mesmo princípio
/// AB-I6-012 de <c>DisposeReconciliationExceptionCommand</c>): identidade e papéis efetivos são sempre
/// resolvidos server-side pelo use case a partir de <see cref="IAuthenticatedActorAccessor"/>.
/// </summary>
public sealed record IssueReconciliationCertificateCommand(
    TenantScope Scope,
    WaveId Wave,
    PurviewImportJobName PlannedJobName,
    CorrelationId Correlation);

/// <summary>
/// RBAC server-side da emissão de certificate (item 19 do work order): resolve o catálogo concreto de
/// papéis do portal (<see cref="PortalRoles"/>) — emitir um certificate é uma decisão de aprovação
/// operacional, nunca uma ação de rotina, mesmo par de papéis que já decide dispositions (Passo 4).
/// </summary>
internal static class ReconciliationCertificateAuthorization
{
    private static readonly string[] IssueRolesByPrecedence = [PortalRoles.Administrator, PortalRoles.Approver];

    /// <summary>Exige que os papéis EFETIVOS do ator autenticado contenham ao menos um papel de emissão conhecido.</summary>
    /// <exception cref="ReconciliationCertificateAuthorizationException">Nenhum papel efetivo do ator está no conjunto autorizado.</exception>
    public static string EnsureCanIssue(IReadOnlyCollection<string> effectiveRoles)
    {
        if (effectiveRoles is { Count: > 0 })
        {
            foreach (var candidate in IssueRolesByPrecedence)
            {
                if (effectiveRoles.Contains(candidate, StringComparer.Ordinal))
                {
                    return candidate;
                }
            }
        }

        throw new ReconciliationCertificateAuthorizationException(
            "Papel não autorizado a emitir reconciliation certificate (fail-closed).");
    }

    /// <summary>Exige um ator identificado (nunca anônimo).</summary>
    /// <exception cref="ReconciliationCertificateAuthorizationException">O ator é vazio/whitespace.</exception>
    public static string RequireActor(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new ReconciliationCertificateAuthorizationException("Emissão anônima não é permitida (ator obrigatório).");
        }

        return actorId.Trim();
    }
}

/// <summary>
/// Emite (ou converge idempotentemente para) a versão vigente do reconciliation certificate de uma wave
/// (AB-I6-013, EPIC-07 Passo 5). ANTES de qualquer cálculo, revalida a cadeia canônica INTEIRA reexecutando
/// a avaliação de reconciliação (<see cref="EvaluateReconciliationUseCase"/>, que por sua vez revalida
/// upload/mapping/binding/execution sem drift via <see cref="PurviewImportJobEvidenceGuard"/> — item 3) e
/// resolve a evidência de mapping/root atual (para o sinal de <c>DUPLICATE_RISK</c>). Identidade e papéis do
/// ator são SEMPRE resolvidos server-side a partir de <see cref="IAuthenticatedActorAccessor"/> — nunca do
/// payload do chamador. Nunca marca wave/projeto <c>COMPLETED</c>, nunca é sign-off final, nunca escreve em
/// EXO/Graph/Purview/EV (STOP-THE-LINE).
/// </summary>
public sealed class IssueReconciliationCertificateUseCase(
    EvaluateReconciliationUseCase evaluator,
    ResolvePurviewMappingEvidenceUseCase evidenceResolver,
    IPurviewMappingCsvStore mappings,
    IReconciliationAssessmentStore assessments,
    IReconciliationExceptionDispositionStore dispositions,
    IReconciliationCertificateStore certificates,
    IClock clock,
    IAuthenticatedActorAccessor actorAccessor)
{
    private readonly EvaluateReconciliationUseCase _evaluator = evaluator;
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IReconciliationAssessmentStore _assessments = assessments;
    private readonly IReconciliationExceptionDispositionStore _dispositions = dispositions;
    private readonly IReconciliationCertificateStore _certificates = certificates;
    private readonly IClock _clock = clock;
    private readonly IAuthenticatedActorAccessor _actorAccessor = actorAccessor;

    /// <exception cref="ReconciliationCertificateAuthorizationException">Ator anônimo ou nenhum papel efetivo autorizado a emitir.</exception>
    /// <exception cref="InvalidOperationException">Nenhum principal autenticado válido no contexto atual.</exception>
    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente/fora de escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, ou drift na cadeia canônica desde a última evidência.</exception>
    /// <exception cref="ReconciliationCertificateStaleChainException">A avaliação ou as dispositions vigentes mudaram concorrentemente durante a emissão.</exception>
    public async Task<ReconciliationCertificate> ExecuteAsync(IssueReconciliationCertificateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // RBAC SEMPRE antes de qualquer acesso a dado de escopo (mesmo princípio anti-enumeração de
        // DisposeReconciliationExceptionUseCase) — identidade/papéis vêm EXCLUSIVAMENTE de
        // IAuthenticatedActorAccessor, nunca do comando.
        var authenticatedActor = _actorAccessor.Current;
        var actor = ReconciliationCertificateAuthorization.RequireActor(authenticatedActor.ActorId);
        var role = ReconciliationCertificateAuthorization.EnsureCanIssue(authenticatedActor.Roles);

        // Item 3: revalida INTEGRALMENTE a cadeia canônica vigente reexecutando a avaliação (que por sua vez
        // revalida upload/mapping/binding/execution sem drift) — nunca confia em uma leitura direta da
        // última avaliação já persistida como se ainda fosse necessariamente vigente.
        var assessment = await _evaluator
            .ExecuteAsync(command.Scope, command.Wave, command.PlannedJobName, command.Correlation, cancellationToken)
            .ConfigureAwait(false);

        var check = await PurviewImportJobEvidenceGuard
            .ResolveAndVerifyNoDriftAsync(_evidenceResolver, _mappings, command.Scope, command.Wave, cancellationToken)
            .ConfigureAwait(false);

        var pstItems = await _assessments
            .GetPstItemsAsync(command.Scope, command.Wave, command.PlannedJobName, assessment.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        var archiveItems = await _assessments
            .GetArchiveItemsAsync(command.Scope, command.Wave, command.PlannedJobName, assessment.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);
        var currentDecisions = await _dispositions
            .GetCurrentDecisionsForAssessmentAsync(command.Scope, command.Wave, command.PlannedJobName, assessment.AssessmentVersion, cancellationToken)
            .ConfigureAwait(false);

        var backlog = ReconciliationExceptionWaveBacklog.From(assessment.AssessmentVersion, pstItems, archiveItems, currentDecisions);
        var summary = ReconciliationWaveSummary.From(pstItems, archiveItems);
        var completeness = ReconciliationCertificateEvidenceCompleteness.From(summary);
        var deviations = ReconciliationCertificateRules.BuildDeviationSummary(backlog);
        var deviationsSha256 = ReconciliationCertificateDeviationsHash.Compute(deviations);
        var decisionsStateFingerprint = ReconciliationExceptionDecisionsStateHash.Compute(currentDecisions);

        // Item 27: DUPLICATE_RISK — a evidência de mapping/root desta tentativa diverge da evidência de
        // mapping/root de uma tentativa (PlannedJobName) DIFERENTE já certificada para a MESMA onda.
        var priorAttempt = await _certificates
            .GetLatestForWaveAcrossOtherAttemptsAsync(command.Scope, command.Wave, command.PlannedJobName, cancellationToken)
            .ConfigureAwait(false);
        var duplicateRiskDetected = priorAttempt is not null && priorAttempt.MappingFingerprint != check.Fingerprint.Value;

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected);

        return await _certificates.IssueOrConvergeAsync(
            command.Scope,
            command.Wave,
            command.PlannedJobName,
            assessment.AssessmentVersion,
            assessment.SourceFingerprint,
            check.Fingerprint.Value,
            decisionsStateFingerprint,
            result,
            completeness.TotalItemCount,
            completeness.IncompleteItemCount,
            deviations.Count,
            deviationsSha256,
            duplicateRiskDetected,
            actor,
            role,
            command.Correlation,
            _clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }
}
