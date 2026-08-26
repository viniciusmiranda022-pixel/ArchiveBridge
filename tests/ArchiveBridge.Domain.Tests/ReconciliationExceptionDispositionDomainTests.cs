using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I6-010 — <see cref="ReconciliationExceptionDispositionRules"/> (transições permitidas, catálogo de
/// motivos fechado, BlockedIntegrity/MatchedWithinEvidence nunca dispositionáveis),
/// <see cref="ReconciliationExceptionDecision"/> (Create/Rehydrate tamper-evident, convergência idempotente
/// por <see cref="ReconciliationExceptionDecision.DecisionFingerprint"/>) e
/// <see cref="ReconciliationExceptionWaveBacklog"/> (backlog derivado, contagens explícitas, itens Matched
/// nunca aparecem). STOP-THE-LINE: nenhum tipo deste Passo produz/referencia
/// <see cref="Domain.Reconciliation.ReconciliationOutcome"/> nem altera o resultado técnico de origem.
/// </summary>
public sealed class ReconciliationExceptionDispositionDomainTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly WaveId Wave = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly PurviewImportJobName PlannedJobName = PurviewImportJobName.FromPersistedValue("ab-imp-0000000000000000-1");
    private static readonly Sha256Hash AssessmentFingerprint = new(new string('a', 64));
    private static readonly DateTimeOffset DecidedAt = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static ReconciliationExceptionDecision Decision(
        ReconciliationDisposition technical = ReconciliationDisposition.Mismatch,
        int decisionVersion = 1,
        ReconciliationExceptionDecisionStatus status = ReconciliationExceptionDecisionStatus.AcceptedException,
        ReconciliationExceptionReasonCode reason = ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy,
        string? comment = null,
        string decidedBy = "approver@contoso.com",
        string itemKey = "p_aaaa_part001.pst",
        ReconciliationExceptionItemKind kind = ReconciliationExceptionItemKind.Pst) =>
        ReconciliationExceptionDecision.Create(
            Tenant, Project, Wave, PlannedJobName, assessmentVersion: 1, AssessmentFingerprint, kind, itemKey, technical,
            decisionVersion, status, reason, ReconciliationExceptionReasonCodeCatalog.CurrentVersion, comment, decidedBy,
            "Approver", Correlation, DecidedAt);

    // ---- ReconciliationExceptionDispositionRules.EnsureDispositionable ----

    [Fact]
    public void EnsureDispositionableRejectsMatchedWithinEvidence()
    {
        Assert.Throws<ReconciliationExceptionNotDispositionableException>(
            () => ReconciliationExceptionDispositionRules.EnsureDispositionable(ReconciliationDisposition.MatchedWithinEvidence));
    }

    [Fact]
    public void EnsureDispositionableRejectsBlockedIntegrity()
    {
        Assert.Throws<ReconciliationExceptionNotDispositionableException>(
            () => ReconciliationExceptionDispositionRules.EnsureDispositionable(ReconciliationDisposition.BlockedIntegrity));
    }

    [Theory]
    [InlineData(ReconciliationDisposition.Mismatch)]
    [InlineData(ReconciliationDisposition.IncompleteEvidence)]
    [InlineData(ReconciliationDisposition.ExtraInProvider)]
    public void EnsureDispositionableAcceptsGenuineExceptions(ReconciliationDisposition technical)
    {
        var exception = Record.Exception(() => ReconciliationExceptionDispositionRules.EnsureDispositionable(technical));
        Assert.Null(exception);
    }

    // ---- ReconciliationExceptionDispositionRules.EnsureStatusIsExplicitlyDecidable ----

    [Fact]
    public void EnsureStatusIsExplicitlyDecidableRejectsPending()
    {
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(
            () => ReconciliationExceptionDispositionRules.EnsureStatusIsExplicitlyDecidable(ReconciliationExceptionDecisionStatus.Pending));
    }

    // ---- ReconciliationExceptionDispositionRules.RequiresElevatedAuthorization ----

    [Fact]
    public void RequiresElevatedAuthorizationOnlyForIncompleteEvidenceAcceptedException()
    {
        Assert.True(ReconciliationExceptionDispositionRules.RequiresElevatedAuthorization(
            ReconciliationDisposition.IncompleteEvidence, ReconciliationExceptionDecisionStatus.AcceptedException));
        Assert.False(ReconciliationExceptionDispositionRules.RequiresElevatedAuthorization(
            ReconciliationDisposition.IncompleteEvidence, ReconciliationExceptionDecisionStatus.RemediationRequired));
        Assert.False(ReconciliationExceptionDispositionRules.RequiresElevatedAuthorization(
            ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.AcceptedException));
    }

    // ---- ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed ----

    [Fact]
    public void EnsureReasonCodeAllowedRejectsUnknownCatalogVersion()
    {
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() =>
            ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
                ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.AcceptedException,
                ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy, reasonCodeCatalogVersion: 99));
    }

    [Fact]
    public void EnsureReasonCodeAllowedRejectsIncompleteEvidenceAcceptedWithAGenericReason()
    {
        // Item 12: a única marca aceita para aceitar IncompleteEvidence é o motivo explícito dedicado —
        // um motivo genérico (usado para Mismatch/ExtraInProvider) nunca basta.
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() =>
            ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
                ReconciliationDisposition.IncompleteEvidence, ReconciliationExceptionDecisionStatus.AcceptedException,
                ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy, ReconciliationExceptionReasonCodeCatalog.CurrentVersion));
    }

    [Fact]
    public void EnsureReasonCodeAllowedAcceptsTheDedicatedIncompleteEvidenceReason()
    {
        var exception = Record.Exception(() => ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
            ReconciliationDisposition.IncompleteEvidence, ReconciliationExceptionDecisionStatus.AcceptedException,
            ReconciliationExceptionReasonCode.IncompleteEvidenceAcceptedByExplicitOperationalPolicy,
            ReconciliationExceptionReasonCodeCatalog.CurrentVersion));
        Assert.Null(exception);
    }

    [Fact]
    public void EnsureReasonCodeAllowedRejectsRemediationReasonForAnAcceptedExceptionStatus()
    {
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() =>
            ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
                ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.AcceptedException,
                ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired, ReconciliationExceptionReasonCodeCatalog.CurrentVersion));
    }

    [Fact]
    public void EnsureReasonCodeAllowedRejectsAnUnknownReasonCodeValue()
    {
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() =>
            ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
                ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.Rejected,
                (ReconciliationExceptionReasonCode)200, ReconciliationExceptionReasonCodeCatalog.CurrentVersion));
    }

    [Fact]
    public void EnsureReasonCodeAllowedAcceptsRemediationRequiredForMismatch()
    {
        var exception = Record.Exception(() => ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
            ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.RemediationRequired,
            ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired, ReconciliationExceptionReasonCodeCatalog.CurrentVersion));
        Assert.Null(exception);
    }

    [Fact]
    public void EnsureReasonCodeAllowedAcceptsRejectedWithTheDedicatedReason()
    {
        var exception = Record.Exception(() => ReconciliationExceptionDispositionRules.EnsureReasonCodeAllowed(
            ReconciliationDisposition.Mismatch, ReconciliationExceptionDecisionStatus.Rejected,
            ReconciliationExceptionReasonCode.DecisionRejectedInsufficientJustification, ReconciliationExceptionReasonCodeCatalog.CurrentVersion));
        Assert.Null(exception);
    }

    // ---- ReconciliationExceptionDecision.Create/Rehydrate ----

    [Fact]
    public void CreateComputesAFingerprintAndHash()
    {
        var decision = Decision();
        Assert.False(string.IsNullOrWhiteSpace(decision.DecisionFingerprint.Value));
        Assert.False(string.IsNullOrWhiteSpace(decision.DecisionHash.Value));
    }

    [Fact]
    public void TheSameDecisionContentAlwaysProducesTheSameFingerprintRegardlessOfDecisionVersion()
    {
        var first = Decision(decisionVersion: 1);
        var second = Decision(decisionVersion: 7);

        Assert.Equal(first.DecisionFingerprint, second.DecisionFingerprint);
        Assert.NotEqual(first.DecisionHash, second.DecisionHash); // O hash cobre a versão — nunca idêntico entre versões diferentes.
    }

    [Theory]
    [InlineData(ReconciliationExceptionDecisionStatus.RemediationRequired)]
    [InlineData(ReconciliationExceptionDecisionStatus.Rejected)]
    public void ADifferentStatusProducesADifferentFingerprint(ReconciliationExceptionDecisionStatus otherStatus)
    {
        var accepted = Decision(status: ReconciliationExceptionDecisionStatus.AcceptedException);
        var other = Decision(status: otherStatus, reason: otherStatus == ReconciliationExceptionDecisionStatus.Rejected
            ? ReconciliationExceptionReasonCode.DecisionRejectedInsufficientJustification
            : ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired);

        Assert.NotEqual(accepted.DecisionFingerprint, other.DecisionFingerprint);
    }

    [Fact]
    public void ADifferentActorProducesADifferentFingerprint()
    {
        var first = Decision(decidedBy: "approver-one@contoso.com");
        var second = Decision(decidedBy: "approver-two@contoso.com");

        Assert.NotEqual(first.DecisionFingerprint, second.DecisionFingerprint);
    }

    [Fact]
    public void RehydrateSucceedsWhenFingerprintAndHashMatchThePersistedValues()
    {
        var created = Decision();
        var rehydrated = ReconciliationExceptionDecision.Rehydrate(
            created.Tenant, created.Project, created.Wave, created.PlannedJobName, created.AssessmentVersion,
            created.AssessmentSourceFingerprint, created.ItemKind, created.ItemKey, created.TechnicalDisposition,
            created.DecisionVersion, created.Status, created.ReasonCode, created.ReasonCodeCatalogVersion, created.Comment,
            created.DecidedBy, created.DecidedByRole, created.Correlation, created.DecidedAtUtc, created.DecisionFingerprint,
            created.DecisionHash);

        Assert.Equal(created, rehydrated);
    }

    [Fact]
    public void RehydrateFailsClosedWhenTheStatusWasTamperedAfterPersistence()
    {
        var created = Decision(status: ReconciliationExceptionDecisionStatus.AcceptedException);

        // Simula uma linha adulterada: status divergente do que o hash/fingerprint originais cobriam.
        Assert.Throws<ReconciliationIntegrityViolationException>(() => ReconciliationExceptionDecision.Rehydrate(
            created.Tenant, created.Project, created.Wave, created.PlannedJobName, created.AssessmentVersion,
            created.AssessmentSourceFingerprint, created.ItemKind, created.ItemKey, created.TechnicalDisposition,
            created.DecisionVersion, ReconciliationExceptionDecisionStatus.Rejected, created.ReasonCode, created.ReasonCodeCatalogVersion,
            created.Comment, created.DecidedBy, created.DecidedByRole, created.Correlation, created.DecidedAtUtc,
            created.DecisionFingerprint, created.DecisionHash));
    }

    [Fact]
    public void RehydrateFailsClosedWhenTheDecisionHashWasTamperedButTheFingerprintWasNot()
    {
        var created = Decision();
        var tamperedHash = new Sha256Hash(new string('f', 64));

        Assert.Throws<ReconciliationIntegrityViolationException>(() => ReconciliationExceptionDecision.Rehydrate(
            created.Tenant, created.Project, created.Wave, created.PlannedJobName, created.AssessmentVersion,
            created.AssessmentSourceFingerprint, created.ItemKind, created.ItemKey, created.TechnicalDisposition,
            created.DecisionVersion, created.Status, created.ReasonCode, created.ReasonCodeCatalogVersion, created.Comment,
            created.DecidedBy, created.DecidedByRole, created.Correlation, created.DecidedAtUtc, created.DecisionFingerprint,
            tamperedHash));
    }

    [Fact]
    public void CreateRejectsANonPositiveDecisionVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconciliationExceptionDecision.Create(
            Tenant, Project, Wave, PlannedJobName, assessmentVersion: 1, AssessmentFingerprint,
            ReconciliationExceptionItemKind.Pst, "p_aaaa_part001.pst", ReconciliationDisposition.Mismatch,
            decisionVersion: 0, ReconciliationExceptionDecisionStatus.AcceptedException,
            ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy, ReconciliationExceptionReasonCodeCatalog.CurrentVersion,
            comment: null, "approver@contoso.com", "Approver", Correlation, DecidedAt));
    }

    [Fact]
    public void CreateRejectsAnAnonymousActor()
    {
        Assert.Throws<ArgumentException>(() => ReconciliationExceptionDecision.Create(
            Tenant, Project, Wave, PlannedJobName, assessmentVersion: 1, AssessmentFingerprint,
            ReconciliationExceptionItemKind.Pst, "p_aaaa_part001.pst", ReconciliationDisposition.Mismatch,
            decisionVersion: 1, ReconciliationExceptionDecisionStatus.AcceptedException,
            ReconciliationExceptionReasonCode.ToleratedByOperationalPolicy, ReconciliationExceptionReasonCodeCatalog.CurrentVersion,
            comment: null, decidedBy: "   ", "Approver", Correlation, DecidedAt));
    }

    // ---- Comentário (item 16) ----

    [Fact]
    public void CreateRejectsACommentAboveTheLengthLimit()
    {
        var oversized = new string('x', 501);
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() => Decision(comment: oversized));
    }

    [Fact]
    public void CreateRejectsACommentWithAControlCharacter()
    {
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() => Decision(comment: "linha1linha2"));
    }

    [Theory]
    [InlineData("SharedAccessSignature=sv=2020-08-04&ss=b&sig=abc123")]
    [InlineData("AccountKey=isto-nao-e-um-segredo-real-apenas-fixture-de-teste")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")]
    public void CreateRejectsACommentThatLooksLikeASecretOrToken(string suspectedSecret)
    {
        Assert.Throws<ReconciliationExceptionDispositionValidationException>(() => Decision(comment: suspectedSecret));
    }

    [Fact]
    public void CreateAcceptsAPlainOperationalComment()
    {
        var decision = Decision(comment: "Reimportação já agendada para a próxima janela de manutenção.");
        Assert.Equal("Reimportação já agendada para a próxima janela de manutenção.", decision.Comment);
    }

    [Fact]
    public void CreateNormalizesAnEmptyOrWhitespaceCommentToNull()
    {
        Assert.Null(Decision(comment: "   ").Comment);
        Assert.Null(Decision(comment: "").Comment);
    }

    // ---- ReconciliationExceptionWaveBacklog.From ----

    private static PurviewRemotePstName Remote(char fill) => PurviewRemotePstName.FromPersistedValue($"p_{new string(fill, 32)}_part001.pst");

    [Fact]
    public void FromNeverIncludesAMatchedWithinEvidenceItem()
    {
        var pstItems = new[]
        {
            new PstReconciliationItem(Remote('a'), ReconciliationDisposition.MatchedWithinEvidence, PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0),
            new PstReconciliationItem(Remote('b'), ReconciliationDisposition.Mismatch, PurviewServiceResultRowStatus.Failed, 0, 0, 0, 0),
        };

        var backlog = ReconciliationExceptionWaveBacklog.From(assessmentVersion: 1, pstItems, [], []);

        var entry = Assert.Single(backlog.Entries);
        Assert.Equal(Remote('b').Value, entry.ItemKey);
    }

    [Fact]
    public void FromMarksAnItemWithoutAnyDecisionAsPending()
    {
        var pstItems = new[] { new PstReconciliationItem(Remote('c'), ReconciliationDisposition.Mismatch, PurviewServiceResultRowStatus.Failed, 0, 0, 0, 0) };

        var backlog = ReconciliationExceptionWaveBacklog.From(assessmentVersion: 1, pstItems, [], []);

        var entry = Assert.Single(backlog.Entries);
        Assert.Equal(ReconciliationExceptionDecisionStatus.Pending, entry.CurrentStatus);
        Assert.True(entry.IsDispositionable);
        Assert.Equal(1, backlog.PendingCount);
    }

    [Fact]
    public void FromMarksABlockedIntegrityItemAsNotDispositionableAndCountsItSeparately()
    {
        var archiveItems = new[]
        {
            new ArchiveReconciliationItem(new TargetArchiveId("blocked@contoso.com"), ReconciliationDisposition.BlockedIntegrity, true, true, null, null),
        };

        var backlog = ReconciliationExceptionWaveBacklog.From(assessmentVersion: 1, [], archiveItems, []);

        var entry = Assert.Single(backlog.Entries);
        Assert.False(entry.IsDispositionable);
        Assert.Equal(1, backlog.NotDispositionableCount);
        Assert.Equal(0, backlog.PendingCount);
    }

    [Fact]
    public void FromReflectsTheCurrentDecisionWhenOneExists()
    {
        var pstItems = new[] { new PstReconciliationItem(Remote('d'), ReconciliationDisposition.Mismatch, PurviewServiceResultRowStatus.Failed, 0, 0, 0, 0) };
        var decision = Decision(itemKey: Remote('d').Value, status: ReconciliationExceptionDecisionStatus.RemediationRequired,
            reason: ReconciliationExceptionReasonCode.RemediationScheduledReimportRequired);

        var backlog = ReconciliationExceptionWaveBacklog.From(assessmentVersion: 1, pstItems, [], [decision]);

        var entry = Assert.Single(backlog.Entries);
        Assert.Equal(ReconciliationExceptionDecisionStatus.RemediationRequired, entry.CurrentStatus);
        Assert.Equal(1, backlog.RemediationRequiredCount);
        Assert.Equal(0, backlog.PendingCount);
    }

    [Fact]
    public void FromCountsEachStatusIndependentlyAcrossPstAndArchiveItems()
    {
        var pstItems = new[]
        {
            new PstReconciliationItem(Remote('e'), ReconciliationDisposition.Mismatch, PurviewServiceResultRowStatus.Failed, 0, 0, 0, 0),
            new PstReconciliationItem(Remote('f'), ReconciliationDisposition.IncompleteEvidence, null, null, null, null, null),
        };
        var archiveItems = new[]
        {
            new ArchiveReconciliationItem(new TargetArchiveId("extra1@contoso.com"), ReconciliationDisposition.ExtraInProvider, true, true, null, null),
        };
        var decisions = new[]
        {
            Decision(itemKey: Remote('e').Value, status: ReconciliationExceptionDecisionStatus.AcceptedException),
            Decision(
                itemKey: Remote('f').Value, technical: ReconciliationDisposition.IncompleteEvidence,
                status: ReconciliationExceptionDecisionStatus.Rejected, reason: ReconciliationExceptionReasonCode.DecisionRejectedInsufficientJustification),
        };

        var backlog = ReconciliationExceptionWaveBacklog.From(assessmentVersion: 1, pstItems, archiveItems, decisions);

        Assert.Equal(3, backlog.Entries.Count);
        Assert.Equal(1, backlog.AcceptedExceptionCount);
        Assert.Equal(1, backlog.RejectedCount);
        Assert.Equal(1, backlog.PendingCount); // O item de archive ExtraInProvider nunca decidido.
    }
}
