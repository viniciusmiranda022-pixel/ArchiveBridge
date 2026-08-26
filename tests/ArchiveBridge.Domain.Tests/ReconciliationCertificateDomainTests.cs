using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I6-013 — <see cref="ReconciliationCertificateRules"/> (precedência determinística do resultado
/// canônico), <see cref="ReconciliationCertificate"/> (Create/Rehydrate tamper-evident, convergência
/// idempotente por <see cref="ReconciliationCertificate.EvaluationFingerprint"/>),
/// <see cref="ReconciliationCertificateDeviationsHash"/>/<see cref="ReconciliationExceptionDecisionsStateHash"/>
/// (agregação determinística ORDEM-INDEPENDENTE). STOP-THE-LINE: nenhum tipo deste Passo marca wave/projeto
/// COMPLETED, faz sign-off final ou chama adapter de write.
/// </summary>
public sealed class ReconciliationCertificateDomainTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly WaveId Wave = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly PurviewImportJobName PlannedJobName = PurviewImportJobName.FromPersistedValue("ab-imp-0000000000000000-1");
    private static readonly Sha256Hash AssessmentFingerprint = new(new string('a', 64));
    private static readonly Sha256Hash MappingFingerprint = new(new string('b', 64));
    private static readonly Sha256Hash DeviationsSha256 = new(new string('c', 64));
    private static readonly Sha256Hash DecisionsStateFingerprint = new(new string('d', 64));
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    // ---- ReconciliationCertificateRules.DetermineResult ----

    [Fact]
    public void DetermineResultIsPassWithNoExceptionsAndCompleteEvidence()
    {
        var backlog = Backlog();
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 0);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: false);

        Assert.Equal(ReconciliationOutcome.Pass, result);
    }

    [Fact]
    public void DetermineResultIsPassWithExplainedExceptionsWhenAllTechnicalExceptionsAreAccepted()
    {
        var backlog = Backlog(
            Entry(ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.AcceptedException, isDispositionable: true),
            Entry(ReconciliationDisposition.ExtraInProvider, ReconciliationExceptionDecisionStatus.AcceptedException, isDispositionable: true));
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 0);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: false);

        Assert.Equal(ReconciliationOutcome.PassWithExplainedExceptions, result);
    }

    [Theory]
    [InlineData(ReconciliationExceptionDecisionStatus.Pending)]
    [InlineData(ReconciliationExceptionDecisionStatus.RemediationRequired)]
    [InlineData(ReconciliationExceptionDecisionStatus.Rejected)]
    public void DetermineResultIsFailWhenATechnicalExceptionIsNotAcceptedException(ReconciliationExceptionDecisionStatus status)
    {
        var backlog = Backlog(Entry(ReconciliationDisposition.Mismatch, status, isDispositionable: true));
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 0);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: false);

        Assert.Equal(ReconciliationOutcome.Fail, result);
    }

    [Fact]
    public void DetermineResultIsInconclusiveWhenEvidenceIsIncompleteEvenWithAnAcceptedDispositionOnTheIncompleteItem()
    {
        // Item 4/36: aceitar o RISCO OPERACIONAL (AcceptedException) de um item IncompleteEvidence nunca
        // torna a EVIDÊNCIA completa — os dois conceitos são deliberadamente independentes.
        var backlog = Backlog(Entry(ReconciliationDisposition.IncompleteEvidence, ReconciliationExceptionDecisionStatus.AcceptedException, isDispositionable: true));
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 1);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: false);

        Assert.Equal(ReconciliationOutcome.Inconclusive, result);
    }

    [Fact]
    public void DetermineResultIsFailWhenBlockedIntegrityIsPresentEvenWhenOtherExceptionsAreAllAccepted()
    {
        // Item 5: BlockedIntegrity é indeclinável — nunca dispositionable — e prevalece mesmo quando TODAS
        // as demais exceções da mesma avaliação já têm disposition aceita.
        var backlog = Backlog(
            Entry(ReconciliationDisposition.BlockedIntegrity, ReconciliationExceptionDecisionStatus.Pending, isDispositionable: false),
            Entry(ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.AcceptedException, isDispositionable: true));
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 0);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: false);

        Assert.Equal(ReconciliationOutcome.Fail, result);
    }

    [Fact]
    public void DetermineResultIsInconclusiveWhenTheWaveHasNoCanonicalItemsAtAll()
    {
        var backlog = Backlog();
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 0, IncompleteItemCount: 0);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: false);

        Assert.Equal(ReconciliationOutcome.Inconclusive, result);
    }

    [Fact]
    public void DetermineResultIsDuplicateRiskAndTakesPrecedenceOverAnOtherwisePassingEvaluation()
    {
        // Item 63: DUPLICATE_RISK tem precedência bloqueadora sobre sucesso — mesmo com evidência 100%
        // completa e zero exceções materiais, um risco de duplicidade comprovado nunca vira PASS.
        var backlog = Backlog();
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 0);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: true);

        Assert.Equal(ReconciliationOutcome.DuplicateRisk, result);
    }

    [Fact]
    public void DetermineResultIsDuplicateRiskEvenWhenBlockedIntegrityAndIncompleteEvidenceAreAlsoPresent()
    {
        var backlog = Backlog(Entry(ReconciliationDisposition.BlockedIntegrity, ReconciliationExceptionDecisionStatus.Pending, isDispositionable: false));
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 3);

        var result = ReconciliationCertificateRules.DetermineResult(completeness, backlog, duplicateRiskDetected: true);

        Assert.Equal(ReconciliationOutcome.DuplicateRisk, result);
    }

    [Fact]
    public void DetermineResultThrowsOnNullArguments()
    {
        var completeness = new ReconciliationCertificateEvidenceCompleteness(1, 0);
        var backlog = Backlog();

        Assert.Throws<ArgumentNullException>(() => ReconciliationCertificateRules.DetermineResult(null!, backlog, false));
        Assert.Throws<ArgumentNullException>(() => ReconciliationCertificateRules.DetermineResult(completeness, null!, false));
    }

    // ---- ReconciliationCertificateRules.BuildDeviationSummary ----

    [Fact]
    public void BuildDeviationSummaryClassifiesEveryNonMatchedItemDeterministically()
    {
        var backlog = Backlog(
            Entry(ReconciliationDisposition.IncompleteEvidence, ReconciliationExceptionDecisionStatus.Pending, isDispositionable: true, itemKey: "a"),
            Entry(ReconciliationDisposition.BlockedIntegrity, ReconciliationExceptionDecisionStatus.Pending, isDispositionable: false, itemKey: "b"),
            Entry(ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.AcceptedException, isDispositionable: true, itemKey: "c"),
            Entry(ReconciliationDisposition.ExtraInProvider, ReconciliationExceptionDecisionStatus.RemediationRequired, isDispositionable: true, itemKey: "d"));

        var deviations = ReconciliationCertificateRules.BuildDeviationSummary(backlog);

        Assert.Equal(4, deviations.Count);
        Assert.Equal(ReconciliationCertificateDeviationCode.IncompleteEvidence, deviations.Single(d => d.ItemKey == "a").Code);
        Assert.Equal(ReconciliationCertificateDeviationCode.BlockedIntegrity, deviations.Single(d => d.ItemKey == "b").Code);
        Assert.Equal(ReconciliationCertificateDeviationCode.ExplainedException, deviations.Single(d => d.ItemKey == "c").Code);
        Assert.Equal(ReconciliationCertificateDeviationCode.UnexplainedException, deviations.Single(d => d.ItemKey == "d").Code);
    }

    // ---- ReconciliationCertificateDeviationsHash / ReconciliationExceptionDecisionsStateHash: ordem-independência ----

    [Fact]
    public void DeviationsHashIsOrderIndependent()
    {
        var a = new ReconciliationCertificateDeviationEntry(ReconciliationExceptionItemKind.Pst, "x", ReconciliationDisposition.Mismatch, ReconciliationCertificateDeviationCode.UnexplainedException);
        var b = new ReconciliationCertificateDeviationEntry(ReconciliationExceptionItemKind.Archive, "y", ReconciliationDisposition.IncompleteEvidence, ReconciliationCertificateDeviationCode.IncompleteEvidence);

        var forward = ReconciliationCertificateDeviationsHash.Compute([a, b]);
        var reversed = ReconciliationCertificateDeviationsHash.Compute([b, a]);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void DeviationsHashChangesWhenAnEntryCodeChanges()
    {
        var explained = new ReconciliationCertificateDeviationEntry(ReconciliationExceptionItemKind.Pst, "x", ReconciliationDisposition.Mismatch, ReconciliationCertificateDeviationCode.ExplainedException);
        var unexplained = explained with { Code = ReconciliationCertificateDeviationCode.UnexplainedException };

        var explainedHash = ReconciliationCertificateDeviationsHash.Compute([explained]);
        var unexplainedHash = ReconciliationCertificateDeviationsHash.Compute([unexplained]);

        Assert.NotEqual(explainedHash, unexplainedHash);
    }

    [Fact]
    public void DecisionsStateHashIsOrderIndependent()
    {
        var first = Decision(itemKey: "a", decisionVersion: 1);
        var second = Decision(itemKey: "b", decisionVersion: 1);

        var forward = ReconciliationExceptionDecisionsStateHash.Compute([first, second]);
        var reversed = ReconciliationExceptionDecisionsStateHash.Compute([second, first]);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void DecisionsStateHashChangesWhenADecisionFingerprintChanges()
    {
        var accepted = Decision(status: ReconciliationExceptionDecisionStatus.AcceptedException);
        var remediation = Decision(status: ReconciliationExceptionDecisionStatus.RemediationRequired, reason: ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired);

        var acceptedHash = ReconciliationExceptionDecisionsStateHash.Compute([accepted]);
        var remediationHash = ReconciliationExceptionDecisionsStateHash.Compute([remediation]);

        Assert.NotEqual(acceptedHash, remediationHash);
    }

    // ---- ReconciliationCertificate.Create / Rehydrate ----

    [Fact]
    public void CreateComputesAConsistentCertificateHashAndEvaluationFingerprint()
    {
        var certificate = Certificate();

        var rehydrated = ReconciliationCertificate.Rehydrate(
            Tenant, Project, Wave, PlannedJobName, certificate.CertificateVersion, certificate.AssessmentVersion,
            certificate.AssessmentSourceFingerprint, certificate.MappingFingerprint, certificate.Result,
            certificate.TotalItemCount, certificate.IncompleteItemCount, certificate.DeviationCount,
            certificate.DeviationsSha256, certificate.DecisionsStateFingerprint, certificate.DuplicateRiskDetected,
            certificate.IssuedBy, certificate.IssuedByRole, certificate.Correlation, certificate.GeneratedAtUtc,
            certificate.SchemaVersion, certificate.CertificateHash);

        Assert.Equal(certificate.CertificateHash, rehydrated.CertificateHash);
        Assert.Equal(certificate.EvaluationFingerprint, rehydrated.EvaluationFingerprint);
    }

    [Fact]
    public void RehydrateThrowsWhenAnyPersistedFieldWasTamperedWith()
    {
        var certificate = Certificate();

        Assert.Throws<ReconciliationCertificateIntegrityViolationException>(() => ReconciliationCertificate.Rehydrate(
            Tenant, Project, Wave, PlannedJobName, certificate.CertificateVersion, certificate.AssessmentVersion,
            certificate.AssessmentSourceFingerprint, certificate.MappingFingerprint,
            ReconciliationOutcome.Pass, // tampered: certificate was actually Fail
            certificate.TotalItemCount, certificate.IncompleteItemCount, certificate.DeviationCount,
            certificate.DeviationsSha256, certificate.DecisionsStateFingerprint, certificate.DuplicateRiskDetected,
            certificate.IssuedBy, certificate.IssuedByRole, certificate.Correlation, certificate.GeneratedAtUtc,
            certificate.SchemaVersion, certificate.CertificateHash));
    }

    [Fact]
    public void RehydrateThrowsWhenTheDecisionsStateFingerprintWasTamperedWith()
    {
        // AB-I6-014: decisions_state_fingerprint agora é um campo persistido coberto por certificate_hash —
        // adulterá-lo isoladamente (sem tocar em nenhum outro campo) deve ser detectado fail-closed.
        var certificate = Certificate();

        Assert.Throws<ReconciliationCertificateIntegrityViolationException>(() => ReconciliationCertificate.Rehydrate(
            Tenant, Project, Wave, PlannedJobName, certificate.CertificateVersion, certificate.AssessmentVersion,
            certificate.AssessmentSourceFingerprint, certificate.MappingFingerprint, certificate.Result,
            certificate.TotalItemCount, certificate.IncompleteItemCount, certificate.DeviationCount,
            certificate.DeviationsSha256, new Sha256Hash(new string('9', 64)), // tampered
            certificate.DuplicateRiskDetected, certificate.IssuedBy, certificate.IssuedByRole, certificate.Correlation,
            certificate.GeneratedAtUtc, certificate.SchemaVersion, certificate.CertificateHash));
    }

    [Fact]
    public void EvaluationFingerprintConvergesForTheSameEvidenceRegardlessOfVersionActorOrTimestamp()
    {
        var first = Certificate(certificateVersion: 1, issuedBy: "admin-a@contoso.com", generatedAtUtc: GeneratedAt);
        var second = Certificate(certificateVersion: 7, issuedBy: "admin-b@contoso.com", generatedAtUtc: GeneratedAt.AddDays(3));

        Assert.Equal(first.EvaluationFingerprint, second.EvaluationFingerprint);
        Assert.NotEqual(first.CertificateHash, second.CertificateHash);
    }

    [Fact]
    public void EvaluationFingerprintChangesWhenDuplicateRiskDiffers()
    {
        var withoutRisk = Certificate(duplicateRiskDetected: false);
        var withRisk = Certificate(duplicateRiskDetected: true);

        Assert.NotEqual(withoutRisk.EvaluationFingerprint, withRisk.EvaluationFingerprint);
    }

    [Fact]
    public void EvaluationFingerprintChangesWhenTheDecisionsStateFingerprintDiffersEvenWithTheSameDeviationsSha256()
    {
        // AB-I6-014 (o bug reportado): duas dispositions vigentes DIFERENTES (ex.: outro reason_code/comment/
        // actor) podem preservar a MESMA classificação de desvio resumida (deviationsSha256 igual) — o
        // certificate NUNCA pode tratar isso como replay idêntico; EvaluationFingerprint deve mudar.
        var first = Certificate(decisionsStateFingerprint: new Sha256Hash(new string('d', 64)));
        var second = Certificate(decisionsStateFingerprint: new Sha256Hash(new string('e', 64)));

        Assert.Equal(first.DeviationsSha256, second.DeviationsSha256);
        Assert.NotEqual(first.DecisionsStateFingerprint, second.DecisionsStateFingerprint);
        Assert.NotEqual(first.EvaluationFingerprint, second.EvaluationFingerprint);
        Assert.NotEqual(first.CertificateHash, second.CertificateHash);
    }

    [Fact]
    public void EvidenceCompletenessIsFalseWhenThereAreNoCanonicalItems()
    {
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 0, IncompleteItemCount: 0);

        Assert.False(completeness.IsComplete);
    }

    [Fact]
    public void EvidenceCompletenessIsFalseWhenAnyItemIsIncomplete()
    {
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 1);

        Assert.False(completeness.IsComplete);
    }

    [Fact]
    public void EvidenceCompletenessIsTrueOnlyWhenEveryItemIsResolved()
    {
        var completeness = new ReconciliationCertificateEvidenceCompleteness(TotalItemCount: 10, IncompleteItemCount: 0);

        Assert.True(completeness.IsComplete);
        Assert.Equal(100m, completeness.CompletenessPercent);
    }

    // ---- helpers ----

    private static ReconciliationExceptionWaveBacklog Backlog(params ReconciliationExceptionBacklogEntry[] entries) =>
        new(
            AssessmentVersion: 1,
            PendingCount: entries.Count(e => e.IsDispositionable && e.CurrentStatus == ReconciliationExceptionDecisionStatus.Pending),
            AcceptedExceptionCount: entries.Count(e => e.CurrentStatus == ReconciliationExceptionDecisionStatus.AcceptedException),
            RemediationRequiredCount: entries.Count(e => e.CurrentStatus == ReconciliationExceptionDecisionStatus.RemediationRequired),
            RejectedCount: entries.Count(e => e.CurrentStatus == ReconciliationExceptionDecisionStatus.Rejected),
            NotDispositionableCount: entries.Count(e => !e.IsDispositionable),
            Entries: entries);

    private static ReconciliationExceptionBacklogEntry Entry(
        ReconciliationDisposition technicalDisposition,
        ReconciliationExceptionDecisionStatus currentStatus,
        bool isDispositionable,
        string itemKey = "p_aaaa_part001.pst",
        ReconciliationExceptionItemKind kind = ReconciliationExceptionItemKind.Pst) =>
        new(kind, itemKey, technicalDisposition, isDispositionable, currentStatus, CurrentReasonCode: null, CurrentDecisionVersion: null, CurrentDecidedBy: null, CurrentDecidedAtUtc: null);

    private static ReconciliationExceptionDecision Decision(
        ReconciliationDisposition technical = ReconciliationDisposition.Mismatch,
        int decisionVersion = 1,
        ReconciliationExceptionDecisionStatus status = ReconciliationExceptionDecisionStatus.AcceptedException,
        ReconciliationExceptionReasonCode reason = ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
        string itemKey = "p_aaaa_part001.pst",
        ReconciliationExceptionItemKind kind = ReconciliationExceptionItemKind.Pst) =>
        ReconciliationExceptionDecision.Create(
            Tenant, Project, Wave, PlannedJobName, assessmentVersion: 1, AssessmentFingerprint, kind, itemKey, technical,
            decisionVersion, status, reason, ReconciliationExceptionReasonCodeCatalog.CurrentVersion, comment: null,
            decidedBy: "approver@contoso.com", decidedByRole: "Approver", Correlation, GeneratedAt);

    private static ReconciliationCertificate Certificate(
        int certificateVersion = 1,
        ReconciliationOutcome result = ReconciliationOutcome.Fail,
        bool duplicateRiskDetected = false,
        string issuedBy = "admin@contoso.com",
        DateTimeOffset? generatedAtUtc = null,
        Sha256Hash? decisionsStateFingerprint = null) =>
        ReconciliationCertificate.Create(
            Tenant, Project, Wave, PlannedJobName, certificateVersion, assessmentVersion: 1, AssessmentFingerprint,
            MappingFingerprint, result, totalItemCount: 10, incompleteItemCount: 0, deviationCount: 1, DeviationsSha256,
            decisionsStateFingerprint ?? DecisionsStateFingerprint, duplicateRiskDetected, issuedBy, "Administrator",
            Correlation, generatedAtUtc ?? GeneratedAt);
}
