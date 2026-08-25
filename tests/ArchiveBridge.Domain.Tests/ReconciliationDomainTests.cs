using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I6-007 — <see cref="ReconciliationPstCorrelation"/>/<see cref="ReconciliationArchiveCorrelation"/>
/// (puras, fail-closed em entrada estruturalmente inválida, nunca convertem ausência em match/zero),
/// <see cref="ReconciliationPstItemsHash"/>/<see cref="ReconciliationArchiveItemsHash"/> (determinísticos,
/// independentes de ordem), <see cref="ReconciliationAssessment"/> (Create/Rehydrate tamper-evident,
/// convergência idempotente por <see cref="ReconciliationAssessment.SourceFingerprint"/>) e
/// <see cref="ReconciliationWaveSummary"/> (contagens explícitas derivadas, nunca persistidas de forma
/// redundante). STOP-THE-LINE: nenhum tipo deste Passo produz/referencia
/// <see cref="Domain.Reconciliation.ReconciliationOutcome"/>.
/// </summary>
public sealed class ReconciliationDomainTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly WaveId Wave = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly CorrelationId Correlation = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly PurviewImportJobName PlannedJobName = PurviewImportJobName.FromPersistedValue("ab-imp-0000000000000000-1");
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private static PurviewRemotePstName Remote(string hex, int part = 1) =>
        PurviewRemotePstName.FromPersistedValue($"p_{hex}_part{part:D3}.pst");

    private static PurviewServiceResultRow Row(
        PurviewRemotePstName remote,
        PurviewServiceResultRowStatus status = PurviewServiceResultRowStatus.Succeeded,
        long? importedItems = 10,
        long? importedBytes = 2048,
        long? skipped = 0,
        long? corrupted = 0) =>
        new(remote, status, importedItems, importedBytes, skipped, corrupted);

    // ---- ReconciliationPstCorrelation ----

    [Fact]
    public void CorrelateMarksAFullyConclusiveSucceededRowAsMatchedWithinEvidence()
    {
        var remote = Remote(new string('a', 32));
        var items = ReconciliationPstCorrelation.Correlate([remote], [Row(remote)]);

        var item = Assert.Single(items);
        Assert.Equal(ReconciliationDisposition.MatchedWithinEvidence, item.Disposition);
        Assert.Equal(PurviewServiceResultRowStatus.Succeeded, item.ObservedStatus);
    }

    [Fact]
    public void CorrelateMarksAnExpectedPstAbsentFromTheProviderResultAsIncompleteEvidenceNeverAsMismatch()
    {
        var remote = Remote(new string('b', 32));
        var items = ReconciliationPstCorrelation.Correlate([remote], []);

        var item = Assert.Single(items);
        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.Null(item.ObservedStatus);
        Assert.Null(item.ImportedItemCount);
    }

    [Fact]
    public void CorrelateMarksAnObservedRowOutsideTheExpectedSetAsExtraInProviderNeverSilentlyDropped()
    {
        var expected = Remote(new string('c', 32));
        var extra = Remote(new string('d', 32));
        var items = ReconciliationPstCorrelation.Correlate([expected], [Row(expected), Row(extra)]);

        Assert.Equal(2, items.Count);
        var extraItem = Assert.Single(items, item => item.RemoteName.Value == extra.Value);
        Assert.Equal(ReconciliationDisposition.ExtraInProvider, extraItem.Disposition);
    }

    [Theory]
    [InlineData(PurviewServiceResultRowStatus.Unknown)]
    public void CorrelateNeverConvertsAnUnknownObservedStatusIntoMatchOrZero(PurviewServiceResultRowStatus status)
    {
        var remote = Remote(new string('e', 32));
        var items = ReconciliationPstCorrelation.Correlate([remote], [Row(remote, status, importedItems: null, importedBytes: null)]);

        var item = Assert.Single(items);
        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
    }

    [Fact]
    public void CorrelateNeverConvertsAMissingCounterOnAnOtherwiseSucceededRowIntoMatch()
    {
        var remote = Remote(new string('f', 32));
        var items = ReconciliationPstCorrelation.Correlate(
            [remote], [Row(remote, PurviewServiceResultRowStatus.Succeeded, importedItems: null, importedBytes: 100)]);

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, Assert.Single(items).Disposition);
    }

    [Theory]
    [InlineData(PurviewServiceResultRowStatus.Failed)]
    [InlineData(PurviewServiceResultRowStatus.SkippedOrCorrupted)]
    public void CorrelateRequiresAConcreteObservedDivergenceBeforeMarkingMismatch(PurviewServiceResultRowStatus status)
    {
        var remote = Remote("1".PadLeft(32, '0'));
        var items = ReconciliationPstCorrelation.Correlate([remote], [Row(remote, status)]);

        Assert.Equal(ReconciliationDisposition.Mismatch, Assert.Single(items).Disposition);
    }

    [Fact]
    public void CorrelateFailsClosedWhenObservedRowsContainADuplicateRemoteName()
    {
        var remote = Remote(new string('9', 32));
        Assert.Throws<ReconciliationValidationException>(() => ReconciliationPstCorrelation.Correlate([remote], [Row(remote), Row(remote)]));
    }

    // ---- ReconciliationArchiveCorrelation ----

    private static ExoArchiveStatisticsSnapshot Snapshot(
        TargetArchiveId archive, ExoStatisticsPhase phase, int version, long? itemCount, long? sizeBytes) =>
        ExoArchiveStatisticsSnapshot.Create(
            Tenant, Project, Wave, archive, phase, version, MailboxArchiveStatus.Active,
            exchangeGuid: null, archiveGuid: null, itemCount, sizeBytes, totalDeletedItemSizeBytes: null,
            lastLogonTimeUtc: null, retentionHoldEnabled: null, litigationHoldEnabled: null, autoExpandingArchiveEnabled: null,
            folderCount: 0, foldersSha256: DeterministicHash.Compute(["empty"]), observedAtUtc: CreatedAt,
            correlation: Correlation, createdAtUtc: CreatedAt);

    [Fact]
    public void ArchiveCorrelateIsIncompleteEvidenceWhenAfterIsMissing()
    {
        var archive = new TargetArchiveId("archive-1@contoso.com");
        var before = Snapshot(archive, ExoStatisticsPhase.BeforeImport, 1, 10, 1000);

        var item = ReconciliationArchiveCorrelation.Correlate(archive, before, after: null);

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.True(item.BeforeCaptured);
        Assert.False(item.AfterCaptured);
        Assert.Null(item.ItemCountDelta);
    }

    [Fact]
    public void ArchiveCorrelateIsIncompleteEvidenceWhenBeforeIsMissingAndNeverFabricatesAHistoricalDelta()
    {
        var archive = new TargetArchiveId("archive-2@contoso.com");
        var after = Snapshot(archive, ExoStatisticsPhase.AfterImport, 1, 20, 2000);

        var item = ReconciliationArchiveCorrelation.Correlate(archive, before: null, after);

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.False(item.BeforeCaptured);
        Assert.True(item.AfterCaptured);
        Assert.Null(item.ItemCountDelta);
    }

    [Fact]
    public void ArchiveCorrelateRejectsASnapshotCapturedForADifferentArchiveAsBlockedIntegrity()
    {
        var expectedArchive = new TargetArchiveId("expected@contoso.com");
        var wrongArchiveSnapshot = Snapshot(new TargetArchiveId("attacker@contoso.com"), ExoStatisticsPhase.BeforeImport, 1, 10, 1000);

        var item = ReconciliationArchiveCorrelation.Correlate(expectedArchive, wrongArchiveSnapshot, after: null);

        Assert.Equal(ReconciliationDisposition.BlockedIntegrity, item.Disposition);
    }

    [Fact]
    public void ArchiveCorrelateRejectsASnapshotWithAMismatchedPhaseAsBlockedIntegrity()
    {
        var archive = new TargetArchiveId("phase-mismatch@contoso.com");
        // Rehydrate with an After phase but pass it as the "before" argument — represents a cross-phase
        // snapshot slipping past a caller bug; the correlation function itself must still reject it.
        var wrongPhase = Snapshot(archive, ExoStatisticsPhase.AfterImport, 1, 10, 1000);

        var item = ReconciliationArchiveCorrelation.Correlate(archive, wrongPhase, after: null);

        Assert.Equal(ReconciliationDisposition.BlockedIntegrity, item.Disposition);
    }

    [Fact]
    public void ArchiveCorrelateMarksADecreasedItemCountAsMismatch()
    {
        var archive = new TargetArchiveId("decrease@contoso.com");
        var before = Snapshot(archive, ExoStatisticsPhase.BeforeImport, 1, 100, 10_000);
        var after = Snapshot(archive, ExoStatisticsPhase.AfterImport, 1, 90, 10_000);

        var item = ReconciliationArchiveCorrelation.Correlate(archive, before, after);

        Assert.Equal(ReconciliationDisposition.Mismatch, item.Disposition);
        Assert.Equal(-10, item.ItemCountDelta);
    }

    [Fact]
    public void ArchiveCorrelateMarksANonNegativeDeltaAsMatchedWithinEvidence()
    {
        var archive = new TargetArchiveId("increase@contoso.com");
        var before = Snapshot(archive, ExoStatisticsPhase.BeforeImport, 1, 100, 10_000);
        var after = Snapshot(archive, ExoStatisticsPhase.AfterImport, 1, 110, 12_000);

        var item = ReconciliationArchiveCorrelation.Correlate(archive, before, after);

        Assert.Equal(ReconciliationDisposition.MatchedWithinEvidence, item.Disposition);
        Assert.Equal(10, item.ItemCountDelta);
        Assert.Equal(2000, item.TotalItemSizeBytesDelta);
    }

    [Fact]
    public void ArchiveCorrelateLeavesTheDeltaUnknownWhenEitherSideOfTheMetricIsUnknown()
    {
        var archive = new TargetArchiveId("unknown-metric@contoso.com");
        var before = Snapshot(archive, ExoStatisticsPhase.BeforeImport, 1, itemCount: null, sizeBytes: null);
        var after = Snapshot(archive, ExoStatisticsPhase.AfterImport, 1, itemCount: null, sizeBytes: null);

        var item = ReconciliationArchiveCorrelation.Correlate(archive, before, after);

        Assert.Equal(ReconciliationDisposition.IncompleteEvidence, item.Disposition);
        Assert.Null(item.ItemCountDelta);
        Assert.Null(item.TotalItemSizeBytesDelta);
    }

    // ---- Hashes: determinístico e independente de ordem ----

    [Fact]
    public void PstItemsHashConvergesRegardlessOfInputOrder()
    {
        var itemA = new PstReconciliationItem(Remote(new string('1', 32)), ReconciliationDisposition.MatchedWithinEvidence,
            PurviewServiceResultRowStatus.Succeeded, 1, 2, 0, 0);
        var itemB = new PstReconciliationItem(Remote(new string('2', 32)), ReconciliationDisposition.IncompleteEvidence,
            null, null, null, null, null);

        var first = ReconciliationPstItemsHash.Compute([itemA, itemB]);
        var second = ReconciliationPstItemsHash.Compute([itemB, itemA]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ArchiveItemsHashConvergesRegardlessOfInputOrder()
    {
        var itemA = new ArchiveReconciliationItem(new TargetArchiveId("a@contoso.com"), ReconciliationDisposition.MatchedWithinEvidence, true, true, 1, 2);
        var itemB = new ArchiveReconciliationItem(new TargetArchiveId("b@contoso.com"), ReconciliationDisposition.IncompleteEvidence, false, false, null, null);

        var first = ReconciliationArchiveItemsHash.Compute([itemA, itemB]);
        var second = ReconciliationArchiveItemsHash.Compute([itemB, itemA]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PstItemsHashChangesWhenAnyFieldOfAnItemChanges()
    {
        var remote = Remote(new string('3', 32));
        var matched = new PstReconciliationItem(remote, ReconciliationDisposition.MatchedWithinEvidence, PurviewServiceResultRowStatus.Succeeded, 1, 2, 0, 0);
        var mismatch = new PstReconciliationItem(remote, ReconciliationDisposition.Mismatch, PurviewServiceResultRowStatus.Failed, 1, 2, 0, 0);

        Assert.NotEqual(ReconciliationPstItemsHash.Compute([matched]), ReconciliationPstItemsHash.Compute([mismatch]));
    }

    // ---- ReconciliationAssessment: Create/Rehydrate tamper-evident, convergência idempotente ----

    private static (Sha256Hash PstHash, Sha256Hash ArchiveHash) ChildHashes() =>
        (ReconciliationPstItemsHash.Compute([]), ReconciliationArchiveItemsHash.Compute([]));

    [Fact]
    public void RehydrateReturnsTheSameAssessmentWhenHashesMatch()
    {
        var (pstHash, archiveHash) = ChildHashes();
        var fingerprint = DeterministicHash.Compute(["source-evidence"]);
        var created = ReconciliationAssessment.Create(
            Tenant, Project, Wave, PlannedJobName, 1, fingerprint, 0, pstHash, 0, archiveHash, Correlation, CreatedAt);

        var rehydrated = ReconciliationAssessment.Rehydrate(
            Tenant, Project, Wave, PlannedJobName, created.AssessmentVersion, created.SourceFingerprint, created.PstItemCount,
            created.PstItemsSha256, created.ArchiveItemCount, created.ArchiveItemsSha256, created.Correlation, created.CreatedAtUtc,
            created.AssessmentHash);

        Assert.Equal(created.AssessmentHash, rehydrated.AssessmentHash);
    }

    [Fact]
    public void RehydrateFailsClosedWhenThePersistedAssessmentHashDoesNotMatchTheRecomputedOne()
    {
        var (pstHash, archiveHash) = ChildHashes();
        var fingerprint = DeterministicHash.Compute(["source-evidence"]);
        var tamperedHash = new Sha256Hash(new string('0', 64));

        Assert.Throws<ReconciliationIntegrityViolationException>(() => ReconciliationAssessment.Rehydrate(
            Tenant, Project, Wave, PlannedJobName, 1, fingerprint, 0, pstHash, 0, archiveHash, Correlation, CreatedAt, tamperedHash));
    }

    [Fact]
    public void ComputeSourceFingerprintIsDeterministicAndOrderIndependentAcrossArchiveEvidence()
    {
        var mappingFingerprint = DeterministicHash.Compute(["mapping"]);
        var archiveA = new ReconciliationArchiveEvidenceRef(new TargetArchiveId("a@contoso.com"), 1, DeterministicHash.Compute(["a"]), 2, DeterministicHash.Compute(["a2"]));
        var archiveB = new ReconciliationArchiveEvidenceRef(new TargetArchiveId("b@contoso.com"), null, null, null, null);

        var first = ReconciliationAssessment.ComputeSourceFingerprint(
            Tenant, Project, Wave, PlannedJobName, mappingFingerprint, 3, DeterministicHash.Compute(["report"]), [archiveA, archiveB]);
        var second = ReconciliationAssessment.ComputeSourceFingerprint(
            Tenant, Project, Wave, PlannedJobName, mappingFingerprint, 3, DeterministicHash.Compute(["report"]), [archiveB, archiveA]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeSourceFingerprintChangesWhenTheReportVersionChanges()
    {
        var mappingFingerprint = DeterministicHash.Compute(["mapping"]);
        var first = ReconciliationAssessment.ComputeSourceFingerprint(
            Tenant, Project, Wave, PlannedJobName, mappingFingerprint, reportVersion: 1, reportContentSha256: null, []);
        var second = ReconciliationAssessment.ComputeSourceFingerprint(
            Tenant, Project, Wave, PlannedJobName, mappingFingerprint, reportVersion: 2, reportContentSha256: null, []);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeSourceFingerprintChangesWhenAnArchiveSnapshotVersionChanges()
    {
        var mappingFingerprint = DeterministicHash.Compute(["mapping"]);
        var archive = new TargetArchiveId("archive@contoso.com");
        var first = ReconciliationAssessment.ComputeSourceFingerprint(
            Tenant, Project, Wave, PlannedJobName, mappingFingerprint, null, null,
            [new ReconciliationArchiveEvidenceRef(archive, 1, DeterministicHash.Compute(["v1"]), null, null)]);
        var second = ReconciliationAssessment.ComputeSourceFingerprint(
            Tenant, Project, Wave, PlannedJobName, mappingFingerprint, null, null,
            [new ReconciliationArchiveEvidenceRef(archive, 2, DeterministicHash.Compute(["v2"]), null, null)]);

        Assert.NotEqual(first, second);
    }

    // ---- ReconciliationWaveSummary ----

    [Fact]
    public void WaveSummaryCountsEachDispositionExplicitlyPerCategory()
    {
        var remoteMatched = Remote(new string('4', 32));
        var remoteMismatch = Remote(new string('5', 32));
        var remoteIncomplete = Remote(new string('6', 32));
        var remoteExtra = Remote(new string('7', 32));
        var pstItems = new[]
        {
            new PstReconciliationItem(remoteMatched, ReconciliationDisposition.MatchedWithinEvidence, PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0),
            new PstReconciliationItem(remoteMismatch, ReconciliationDisposition.Mismatch, PurviewServiceResultRowStatus.Failed, null, null, null, null),
            new PstReconciliationItem(remoteIncomplete, ReconciliationDisposition.IncompleteEvidence, null, null, null, null, null),
            new PstReconciliationItem(remoteExtra, ReconciliationDisposition.ExtraInProvider, PurviewServiceResultRowStatus.Succeeded, 1, 1, 0, 0),
        };
        var archiveItems = new[]
        {
            new ArchiveReconciliationItem(new TargetArchiveId("m@contoso.com"), ReconciliationDisposition.MatchedWithinEvidence, true, true, 1, 1),
            new ArchiveReconciliationItem(new TargetArchiveId("b@contoso.com"), ReconciliationDisposition.BlockedIntegrity, true, false, null, null),
        };

        var summary = ReconciliationWaveSummary.From(pstItems, archiveItems);

        Assert.Equal(1, summary.PstMatched);
        Assert.Equal(1, summary.PstMismatch);
        Assert.Equal(1, summary.PstIncomplete);
        Assert.Equal(1, summary.PstExtraInProvider);
        Assert.Equal(0, summary.PstBlockedIntegrity);
        Assert.Equal(1, summary.ArchiveMatched);
        Assert.Equal(1, summary.ArchiveBlockedIntegrity);
        Assert.Equal(0, summary.ArchiveMismatch);
        Assert.Equal(0, summary.ArchiveIncomplete);
    }

    // ---- STOP-THE-LINE: nenhum tipo deste Passo expõe um resultado de reconciliação FINAL ----

    [Fact]
    public void ReconciliationDispositionNeverExposesATerminalPassCertificateOrOutcomeValue()
    {
        var names = Enum.GetNames<ReconciliationDisposition>();
        Assert.DoesNotContain(names, name => name is "Pass" or "PassWithExplainedExceptions" or "Fail" or "DuplicateRisk" or "Certificate" or "Completed");
    }
}
