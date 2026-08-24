using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>Testes de domínio do precheck read-only e do policy/capacity gate Purview (I5/EPIC-06 Passo 1, AB-I5-001).</summary>
public sealed class Slice5PurviewPrecheckGateTests
{
    private static TenantId Tenant => new(Guid.NewGuid());

    private static ProjectId Project => new(Guid.NewGuid());

    private static ArchiveRef ResolvedMailbox(string mailbox = "user01@contoso.com") =>
        new(mailbox, new TargetArchiveId(mailbox));

    private static MailboxPrecheckSnapshot ValidPrecheck(
        MailboxArchiveStatus archiveStatus = MailboxArchiveStatus.Active,
        bool autoExpandingArchiveEnabled = false,
        long? observedAvailableBytes = 200L * 1_000_000_000)
    {
        var now = DateTimeOffset.UtcNow;
        return MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), Tenant, Project, ResolvedMailbox(), 1,
            Guid.NewGuid(), Guid.NewGuid(), archiveStatus, "UserMailbox", autoExpandingArchiveEnabled,
            litigationHoldEnabled: false, retentionHoldEnabled: false, archiveItemCount: 1000,
            archiveTotalSizeBytes: 10L * 1_000_000_000, observedAvailableBytes, now, CorrelationId.New(), now);
    }

    // ---- MailboxPrecheckSnapshot (anti-IDOR + tamper-evidence) --------------------------------------

    [Fact]
    public void ObserveRejectsUnresolvedMailboxIdentity()
    {
        var unresolved = new ArchiveRef("user01@contoso.com");
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<PurviewValidationException>(() => MailboxPrecheckSnapshot.Observe(
            PrecheckSnapshotId.New(), Tenant, Project, unresolved, 1, null, null, MailboxArchiveStatus.Active,
            null, false, false, false, null, null, null, now, CorrelationId.New(), now));
    }

    [Fact]
    public void RehydrateFailsClosedWhenArchiveStatusIsTamperedButHashStaysStale()
    {
        var snapshot = ValidPrecheck();
        Assert.Throws<MailboxPrecheckIntegrityViolationException>(() => MailboxPrecheckSnapshot.Rehydrate(
            snapshot.Id, snapshot.Tenant, snapshot.Project, snapshot.Mailbox, snapshot.Version,
            snapshot.ExchangeGuid, snapshot.ArchiveGuid, MailboxArchiveStatus.None, snapshot.RecipientTypeDetails,
            snapshot.AutoExpandingArchiveEnabled, snapshot.LitigationHoldEnabled, snapshot.RetentionHoldEnabled,
            snapshot.ArchiveItemCount, snapshot.ArchiveTotalSizeBytes, snapshot.ObservedAvailableBytes,
            snapshot.ObservedAtUtc, snapshot.Correlation, snapshot.RecordedAtUtc, snapshot.SnapshotHash));
    }

    [Fact]
    public void RecordThenRehydrateRoundTripsWithMatchingHash()
    {
        var snapshot = ValidPrecheck();
        var rehydrated = MailboxPrecheckSnapshot.Rehydrate(
            snapshot.Id, snapshot.Tenant, snapshot.Project, snapshot.Mailbox, snapshot.Version,
            snapshot.ExchangeGuid, snapshot.ArchiveGuid, snapshot.ArchiveStatus, snapshot.RecipientTypeDetails,
            snapshot.AutoExpandingArchiveEnabled, snapshot.LitigationHoldEnabled, snapshot.RetentionHoldEnabled,
            snapshot.ArchiveItemCount, snapshot.ArchiveTotalSizeBytes, snapshot.ObservedAvailableBytes,
            snapshot.ObservedAtUtc, snapshot.Correlation, snapshot.RecordedAtUtc, snapshot.SnapshotHash);
        Assert.Equal(snapshot.SnapshotHash, rehydrated.SnapshotHash);
    }

    // ---- Target root folder ("/" bloqueado) — invariante reutilizado, não duplicado (item 9) --------

    [Fact]
    public void RootTargetFolderIsRejectedByTheReusedWavesValueObject()
    {
        Assert.Throws<ArgumentException>(() => new TargetRootFolder("/"));
    }

    // ---- PurviewPrecheckGate — capability -------------------------------------------------------------

    [Fact]
    public void GeneralAvailabilityCapabilityWithinAllLimitsIsAllowed()
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(),
            csvRowCount: 10, plannedArchiveImportBytes: 1_000_000_000, [1_000_000_000]);

        Assert.True(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.None, result.Reason);
    }

    [Theory]
    [InlineData(CapabilityUsabilityOutcome.NoEvidence, PurviewPrecheckBlockReason.CapabilityEvidenceMissing)]
    [InlineData(CapabilityUsabilityOutcome.Unknown, PurviewPrecheckBlockReason.CapabilityUnknown)]
    [InlineData(CapabilityUsabilityOutcome.Unsupported, PurviewPrecheckBlockReason.CapabilityUnsupported)]
    [InlineData(CapabilityUsabilityOutcome.NotGeneralAvailability, PurviewPrecheckBlockReason.CapabilityNotGeneralAvailability)]
    [InlineData(CapabilityUsabilityOutcome.Stale, PurviewPrecheckBlockReason.CapabilityEvidenceStale)]
    public void NonUsableCapabilityBlocksBeforeAnyOtherCheck(CapabilityUsabilityOutcome outcome, PurviewPrecheckBlockReason expected)
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, outcome, ValidPrecheck(archiveStatus: MailboxArchiveStatus.None),
            csvRowCount: 10, plannedArchiveImportBytes: 1, [1]);

        Assert.False(result.Allowed);
        Assert.Equal(expected, result.Reason);
    }

    // ---- Archive status ------------------------------------------------------------------------------

    [Theory]
    [InlineData(MailboxArchiveStatus.Unknown)]
    [InlineData(MailboxArchiveStatus.None)]
    [InlineData(MailboxArchiveStatus.Disabled)]
    public void InactiveArchiveBlocks(MailboxArchiveStatus status)
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(archiveStatus: status),
            csvRowCount: 1, plannedArchiveImportBytes: 1, [1]);

        Assert.False(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.ArchiveInactive, result.Reason);
    }

    // ---- CSV row limit (500 permitido, 501 bloqueado) -------------------------------------------------

    [Fact]
    public void ExactlyFiveHundredCsvRowsIsAllowed()
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(),
            csvRowCount: 500, plannedArchiveImportBytes: 1, [1]);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void FiveHundredAndOneCsvRowsIsBlocked()
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(),
            csvRowCount: 501, plannedArchiveImportBytes: 1, [1]);
        Assert.False(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.CsvRowLimitExceeded, result.Reason);
    }

    // ---- Part size boundary (limite duro de política, §20.1) ------------------------------------------

    [Fact]
    public void PartExactlyAtHardLimitIsAllowed()
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(),
            csvRowCount: 1, plannedArchiveImportBytes: PartitionPolicy.RunbookHardPartBytes,
            [PartitionPolicy.RunbookHardPartBytes]);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void PartOneByteAboveHardLimitIsBlocked()
    {
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(),
            csvRowCount: 1, plannedArchiveImportBytes: PartitionPolicy.RunbookHardPartBytes + 1,
            [PartitionPolicy.RunbookHardPartBytes + 1]);
        Assert.False(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.PartExceedsPolicy, result.Reason);
    }

    // ---- Limite principal por archive (100 GB) e auto-expansion NUNCA o eleva (item 8) -----------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannedBytesOverMainArchiveLimitBlocksRegardlessOfAutoExpansion(bool autoExpandingArchiveEnabled)
    {
        var plannedBytes = CapacityRule.OneHundredGigabytesInBytes + 1;
        var precheck = ValidPrecheck(autoExpandingArchiveEnabled: autoExpandingArchiveEnabled, observedAvailableBytes: plannedBytes + 1_000_000_000);

        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, precheck,
            csvRowCount: 1, plannedArchiveImportBytes: plannedBytes, [1_000_000_000]);

        Assert.False(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.MainArchiveImportLimitExceeded, result.Reason);
        Assert.Equal(CapacityRule.AssessmentRequiredCode, result.ReasonCode);
    }

    [Fact]
    public void PlannedBytesExactlyAtMainArchiveLimitIsAllowed()
    {
        var plannedBytes = CapacityRule.OneHundredGigabytesInBytes;
        var precheck = ValidPrecheck(observedAvailableBytes: plannedBytes + PurviewPolicyLimits.DefaultSafetyMarginBytes + 1);

        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, precheck,
            csvRowCount: 1, plannedArchiveImportBytes: plannedBytes, [1_000_000_000]);

        Assert.True(result.Allowed);
    }

    // ---- Margem de capacidade observada (item 10) ------------------------------------------------------

    [Fact]
    public void CapacityNotObservedBlocksFailClosed()
    {
        var precheck = ValidPrecheck(observedAvailableBytes: null);
        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, precheck,
            csvRowCount: 1, plannedArchiveImportBytes: 1, [1]);

        Assert.False(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.CapacityNotObserved, result.Reason);
    }

    [Fact]
    public void PlannedBytesWithinCapacityMarginIsAllowed()
    {
        var limits = PurviewPolicyLimits.Create(
            mainArchiveImportLimitBytes: 1_000_000_000_000, hardPartBytes: 1_000_000_000_000, maxCsvDataRows: 500,
            safetyMarginBytes: 1_000_000_000);
        var precheck = ValidPrecheck(observedAvailableBytes: 10_000_000_000);

        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            limits, CapabilityUsabilityOutcome.Usable, precheck, csvRowCount: 1,
            plannedArchiveImportBytes: 9_000_000_000, [9_000_000_000]);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void PlannedBytesExceedingCapacityMarginIsBlocked()
    {
        var limits = PurviewPolicyLimits.Create(
            mainArchiveImportLimitBytes: 1_000_000_000_000, hardPartBytes: 1_000_000_000_000, maxCsvDataRows: 500,
            safetyMarginBytes: 1_000_000_000);
        var precheck = ValidPrecheck(observedAvailableBytes: 10_000_000_000);

        var result = PurviewPrecheckGate.EvaluateArchiveImport(
            limits, CapabilityUsabilityOutcome.Usable, precheck, csvRowCount: 1,
            plannedArchiveImportBytes: 9_500_000_001, [9_500_000_001]);

        Assert.False(result.Allowed);
        Assert.Equal(PurviewPrecheckBlockReason.CapacityMarginExceeded, result.Reason);
    }

    // ---- Guard clauses --------------------------------------------------------------------------------

    [Fact]
    public void NegativeCsvRowCountThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(), -1, 1, [1]));
    }

    [Fact]
    public void NegativePlannedBytesThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PurviewPrecheckGate.EvaluateArchiveImport(
            PurviewPolicyLimits.RunbookDefault, CapabilityUsabilityOutcome.Usable, ValidPrecheck(), 1, -1, [1]));
    }
}
