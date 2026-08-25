using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I6-005 — <see cref="ExoArchiveFolderStatistic"/>/<see cref="ExoArchiveFolderStatisticsSet"/>
/// (bounded/canonicalizado/fail-closed), <see cref="ExoArchiveFolderStatisticsHash"/> (determinístico,
/// independente de ordem) e <see cref="ExoArchiveStatisticsSnapshot"/> (Create/Rehydrate tamper-evident,
/// convergência idempotente por <see cref="ExoArchiveStatisticsSnapshot.ObservationHash"/>). Campo
/// ausente permanece <see langword="null"/> (Unknown/NotReported), nunca zero/false/data mínima.
/// </summary>
public sealed class ExoArchiveStatisticsDomainTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 24, 9, 0, 5, TimeSpan.Zero);
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ProjectId Project = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly WaveId Wave = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly TargetArchiveId Archive = new("user01@contoso.com");
    private static readonly CorrelationId Correlation = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    private static ExoArchiveFolderStatistic Folder(
        string path = "/Top of Information Store/Inbox",
        string type = "Inbox",
        long? items = 10,
        long? itemsAndSub = 10,
        long? sizeBytes = 2048,
        long? sizeAndSubBytes = 2048,
        DateTimeOffset? oldest = null,
        DateTimeOffset? newest = null) =>
        new(path, type, items, itemsAndSub, sizeBytes, sizeAndSubBytes, oldest, newest);

    // ---- ExoArchiveFolderStatistic ----

    [Fact]
    public void FolderConstructorRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => Folder(path: "   "));
    }

    [Fact]
    public void FolderConstructorRejectsOversizedPath()
    {
        var oversized = new string('a', ExoArchiveFolderStatistic.MaxFolderPathLength + 1);
        Assert.Throws<ArgumentException>(() => Folder(path: oversized));
    }

    [Fact]
    public void FolderConstructorRejectsNegativeCounter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Folder(items: -1));
    }

    [Fact]
    public void FolderConstructorAllowsAllCountersAndDatesNullAsUnknown()
    {
        var folder = Folder(items: null, itemsAndSub: null, sizeBytes: null, sizeAndSubBytes: null, oldest: null, newest: null);

        Assert.Null(folder.ItemsInFolder);
        Assert.Null(folder.ItemsInFolderAndSubfolders);
        Assert.Null(folder.FolderSizeBytes);
        Assert.Null(folder.FolderAndSubfolderSizeBytes);
        Assert.Null(folder.OldestItemReceivedDateUtc);
        Assert.Null(folder.NewestItemReceivedDateUtc);
    }

    [Fact]
    public void FolderConstructorFailsClosedWhenOldestIsAfterNewest()
    {
        var oldest = ObservedAt;
        var newest = ObservedAt.AddDays(-1);

        Assert.Throws<ExoArchiveStatisticsValidationException>(() => Folder(oldest: oldest, newest: newest));
    }

    [Fact]
    public void FolderConstructorAcceptsOldestEqualToNewest()
    {
        var folder = Folder(oldest: ObservedAt, newest: ObservedAt);

        Assert.Equal(folder.NewestItemReceivedDateUtc, folder.OldestItemReceivedDateUtc);
    }

    // ---- ExoArchiveFolderStatisticsSet ----

    [Fact]
    public void CanonicalizeSortsByFolderPathOrdinalRegardlessOfInputOrder()
    {
        var folders = new[] { Folder(path: "/B"), Folder(path: "/A"), Folder(path: "/C") };

        var canonical = ExoArchiveFolderStatisticsSet.Canonicalize(folders);

        Assert.Equal(["/A", "/B", "/C"], canonical.Select(folder => folder.FolderPath));
    }

    [Fact]
    public void CanonicalizeFailsClosedOnDuplicateFolderPath()
    {
        var folders = new[] { Folder(path: "/Inbox"), Folder(path: "/Inbox") };

        Assert.Throws<ExoArchiveStatisticsValidationException>(() => ExoArchiveFolderStatisticsSet.Canonicalize(folders));
    }

    [Fact]
    public void CanonicalizeFailsClosedWhenFolderCountExceedsTheLimit()
    {
        var folders = Enumerable.Range(0, ExoArchiveFolderStatisticsSet.MaxFolders + 1)
            .Select(index => Folder(path: $"/Folder{index}"))
            .ToArray();

        Assert.Throws<ExoArchiveStatisticsValidationException>(() => ExoArchiveFolderStatisticsSet.Canonicalize(folders));
    }

    [Fact]
    public void CanonicalizeAcceptsExactlyTheMaximumFolderCount()
    {
        var folders = Enumerable.Range(0, ExoArchiveFolderStatisticsSet.MaxFolders)
            .Select(index => Folder(path: $"/Folder{index:D4}"))
            .ToArray();

        var canonical = ExoArchiveFolderStatisticsSet.Canonicalize(folders);

        Assert.Equal(ExoArchiveFolderStatisticsSet.MaxFolders, canonical.Count);
    }

    // ---- ExoArchiveFolderStatisticsHash ----

    [Fact]
    public void FolderStatisticsHashIsIndependentOfInputOrder()
    {
        var inOrderA = new[] { Folder(path: "/A"), Folder(path: "/B") };
        var inOrderB = new[] { Folder(path: "/B"), Folder(path: "/A") };

        Assert.Equal(ExoArchiveFolderStatisticsHash.Compute(inOrderA), ExoArchiveFolderStatisticsHash.Compute(inOrderB));
    }

    [Fact]
    public void FolderStatisticsHashChangesWhenAnyCounterChanges()
    {
        var baseline = new[] { Folder(path: "/A", items: 10) };
        var changed = new[] { Folder(path: "/A", items: 11) };

        Assert.NotEqual(ExoArchiveFolderStatisticsHash.Compute(baseline), ExoArchiveFolderStatisticsHash.Compute(changed));
    }

    [Fact]
    public void FolderStatisticsHashDistinguishesNullFromAnyExplicitValue()
    {
        var withNull = new[] { Folder(path: "/A", items: null) };
        var withZero = new[] { Folder(path: "/A", items: 0) };

        Assert.NotEqual(ExoArchiveFolderStatisticsHash.Compute(withNull), ExoArchiveFolderStatisticsHash.Compute(withZero));
    }

    [Fact]
    public void FolderStatisticsHashOfEmptySetIsStable()
    {
        Assert.Equal(ExoArchiveFolderStatisticsHash.Compute([]), ExoArchiveFolderStatisticsHash.Compute([]));
    }

    // ---- ExoArchiveStatisticsSnapshot ----

    private static readonly Guid FixedExchangeGuid = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid FixedArchiveGuid = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static ExoArchiveStatisticsSnapshot CreateSnapshot(
        ExoStatisticsPhase phase = ExoStatisticsPhase.BeforeImport,
        int version = 1,
        long? itemCount = 100,
        bool? retentionHold = false,
        Sha256Hash? foldersSha256 = null,
        int folderCount = 0,
        DateTimeOffset? observedAt = null) =>
        ExoArchiveStatisticsSnapshot.Create(
            Tenant, Project, Wave, Archive, phase, version, MailboxArchiveStatus.Active,
            exchangeGuid: FixedExchangeGuid, archiveGuid: FixedArchiveGuid, itemCount, totalItemSizeBytes: 4096,
            totalDeletedItemSizeBytes: 0, lastLogonTimeUtc: ObservedAt, retentionHoldEnabled: retentionHold,
            litigationHoldEnabled: false, autoExpandingArchiveEnabled: false, folderCount,
            foldersSha256 ?? ExoArchiveFolderStatisticsHash.Compute([]), observedAt ?? ObservedAt, Correlation, CreatedAt);

    [Fact]
    public void CreateRejectsNonPositiveVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(version: 0));
    }

    [Fact]
    public void CreateRejectsNegativeItemCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(itemCount: -1));
    }

    [Fact]
    public void CreateAllowsEveryOptionalFieldAsNullUnknown()
    {
        var snapshot = ExoArchiveStatisticsSnapshot.Create(
            Tenant, Project, Wave, Archive, ExoStatisticsPhase.BeforeImport, 1, MailboxArchiveStatus.Unknown,
            exchangeGuid: null, archiveGuid: null, itemCount: null, totalItemSizeBytes: null,
            totalDeletedItemSizeBytes: null, lastLogonTimeUtc: null, retentionHoldEnabled: null,
            litigationHoldEnabled: null, autoExpandingArchiveEnabled: null, folderCount: 0,
            ExoArchiveFolderStatisticsHash.Compute([]), ObservedAt, Correlation, CreatedAt);

        Assert.Null(snapshot.ExchangeGuid);
        Assert.Null(snapshot.ArchiveGuid);
        Assert.Null(snapshot.ItemCount);
        Assert.Null(snapshot.TotalItemSizeBytes);
        Assert.Null(snapshot.TotalDeletedItemSizeBytes);
        Assert.Null(snapshot.LastLogonTimeUtc);
        Assert.Null(snapshot.RetentionHoldEnabled);
        Assert.Null(snapshot.LitigationHoldEnabled);
        Assert.Null(snapshot.AutoExpandingArchiveEnabled);
        Assert.Equal(MailboxArchiveStatus.Unknown, snapshot.ArchiveStatus);
    }

    [Fact]
    public void ObservationHashIsDeterministicForTheSameLogicalObservation()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot();

        Assert.Equal(first.ObservationHash, second.ObservationHash);
    }

    [Fact]
    public void ObservationHashDiffersWhenPhaseDiffers()
    {
        var before = CreateSnapshot(phase: ExoStatisticsPhase.BeforeImport);
        var after = CreateSnapshot(phase: ExoStatisticsPhase.AfterImport);

        Assert.NotEqual(before.ObservationHash, after.ObservationHash);
    }

    [Fact]
    public void ObservationHashDiffersWhenAFieldGenuinelyChanges()
    {
        var first = CreateSnapshot(itemCount: 100);
        var second = CreateSnapshot(itemCount: 200);

        Assert.NotEqual(first.ObservationHash, second.ObservationHash);
    }

    [Fact]
    public void ObservationHashDiffersWhenNullVersusExplicitFalse()
    {
        var withNull = CreateSnapshot(retentionHold: null);
        var withFalse = CreateSnapshot(retentionHold: false);

        Assert.NotEqual(withNull.ObservationHash, withFalse.ObservationHash);
    }

    [Fact]
    public void ObservationHashIsIndependentOfVersionAndCreatedAt()
    {
        var v1 = CreateSnapshot(version: 1);
        var v2 = ExoArchiveStatisticsSnapshot.Create(
            Tenant, Project, Wave, Archive, ExoStatisticsPhase.BeforeImport, 2, MailboxArchiveStatus.Active,
            v1.ExchangeGuid, v1.ArchiveGuid, v1.ItemCount, v1.TotalItemSizeBytes, v1.TotalDeletedItemSizeBytes,
            v1.LastLogonTimeUtc, v1.RetentionHoldEnabled, v1.LitigationHoldEnabled, v1.AutoExpandingArchiveEnabled,
            v1.FolderCount, v1.FoldersSha256, v1.ObservedAtUtc, Correlation, CreatedAt.AddMinutes(5));

        Assert.Equal(v1.ObservationHash, v2.ObservationHash);
        Assert.NotEqual(v1.SnapshotHash, v2.SnapshotHash);
    }

    [Fact]
    public void RehydrateFailsClosedWhenObservationHashWasTamperedAfterPersistence()
    {
        var snapshot = CreateSnapshot();

        Assert.Throws<ExoArchiveStatisticsIntegrityViolationException>(() => ExoArchiveStatisticsSnapshot.Rehydrate(
            snapshot.Tenant, snapshot.Project, snapshot.Wave, snapshot.Archive, snapshot.Phase, snapshot.SnapshotVersion,
            snapshot.ArchiveStatus, snapshot.ExchangeGuid, snapshot.ArchiveGuid, itemCount: 999, snapshot.TotalItemSizeBytes,
            snapshot.TotalDeletedItemSizeBytes, snapshot.LastLogonTimeUtc, snapshot.RetentionHoldEnabled,
            snapshot.LitigationHoldEnabled, snapshot.AutoExpandingArchiveEnabled, snapshot.FolderCount, snapshot.FoldersSha256,
            snapshot.ObservedAtUtc, snapshot.Correlation, snapshot.CreatedAtUtc, snapshot.ObservationHash, snapshot.SnapshotHash));
    }

    [Fact]
    public void RehydrateFailsClosedWhenSnapshotHashWasTamperedAfterPersistence()
    {
        var snapshot = CreateSnapshot();
        var tamperedSnapshotHash = new Sha256Hash(new string('0', 64));

        Assert.Throws<ExoArchiveStatisticsIntegrityViolationException>(() => ExoArchiveStatisticsSnapshot.Rehydrate(
            snapshot.Tenant, snapshot.Project, snapshot.Wave, snapshot.Archive, snapshot.Phase, snapshot.SnapshotVersion,
            snapshot.ArchiveStatus, snapshot.ExchangeGuid, snapshot.ArchiveGuid, snapshot.ItemCount, snapshot.TotalItemSizeBytes,
            snapshot.TotalDeletedItemSizeBytes, snapshot.LastLogonTimeUtc, snapshot.RetentionHoldEnabled,
            snapshot.LitigationHoldEnabled, snapshot.AutoExpandingArchiveEnabled, snapshot.FolderCount, snapshot.FoldersSha256,
            snapshot.ObservedAtUtc, snapshot.Correlation, snapshot.CreatedAtUtc, snapshot.ObservationHash, tamperedSnapshotHash));
    }

    [Fact]
    public void RehydrateFailsClosedWhenFoldersShaWasTamperedAfterPersistence()
    {
        var snapshot = CreateSnapshot();
        var tamperedFoldersSha = ExoArchiveFolderStatisticsHash.Compute([Folder()]);

        Assert.Throws<ExoArchiveStatisticsIntegrityViolationException>(() => ExoArchiveStatisticsSnapshot.Rehydrate(
            snapshot.Tenant, snapshot.Project, snapshot.Wave, snapshot.Archive, snapshot.Phase, snapshot.SnapshotVersion,
            snapshot.ArchiveStatus, snapshot.ExchangeGuid, snapshot.ArchiveGuid, snapshot.ItemCount, snapshot.TotalItemSizeBytes,
            snapshot.TotalDeletedItemSizeBytes, snapshot.LastLogonTimeUtc, snapshot.RetentionHoldEnabled,
            snapshot.LitigationHoldEnabled, snapshot.AutoExpandingArchiveEnabled, snapshot.FolderCount, tamperedFoldersSha,
            snapshot.ObservedAtUtc, snapshot.Correlation, snapshot.CreatedAtUtc, snapshot.ObservationHash, snapshot.SnapshotHash));
    }

    [Fact]
    public void RehydrateSucceedsWhenAllFieldsAndHashesMatchExactlyWhatWasPersisted()
    {
        var snapshot = CreateSnapshot();

        var rehydrated = ExoArchiveStatisticsSnapshot.Rehydrate(
            snapshot.Tenant, snapshot.Project, snapshot.Wave, snapshot.Archive, snapshot.Phase, snapshot.SnapshotVersion,
            snapshot.ArchiveStatus, snapshot.ExchangeGuid, snapshot.ArchiveGuid, snapshot.ItemCount, snapshot.TotalItemSizeBytes,
            snapshot.TotalDeletedItemSizeBytes, snapshot.LastLogonTimeUtc, snapshot.RetentionHoldEnabled,
            snapshot.LitigationHoldEnabled, snapshot.AutoExpandingArchiveEnabled, snapshot.FolderCount, snapshot.FoldersSha256,
            snapshot.ObservedAtUtc, snapshot.Correlation, snapshot.CreatedAtUtc, snapshot.ObservationHash, snapshot.SnapshotHash);

        Assert.Equal(snapshot, rehydrated);
    }

    [Fact]
    public void PhaseNeverExposesAFinalReconciliationOutcome()
    {
        // AB-I6-005 invariante: EXO stats são observação read-only, nunca PASS/FAIL/certificate/conclusão
        // de onda. O enum de fase só tem duas fases de observação — nenhum valor de resultado final existe.
        var values = Enum.GetValues<ExoStatisticsPhase>();

        Assert.Equal(2, values.Length);
        Assert.Contains(ExoStatisticsPhase.BeforeImport, values);
        Assert.Contains(ExoStatisticsPhase.AfterImport, values);
    }
}
