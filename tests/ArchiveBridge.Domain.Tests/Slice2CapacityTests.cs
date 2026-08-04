using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Tests;

public sealed class Slice2CapacityTests
{
    private const long Limit = CapacityRule.OneHundredGigabytesInBytes; // 100 GB decimais
    private const long OneHundredGibibytes = 100L * 1024 * 1024 * 1024;  // 100 GiB (binário)

    private static WaveEntry Entry(string pst, string mailbox, long size) =>
        new($"/src/{pst}", pst, new ArchiveRef(mailbox), size, 1);

    private static WaveEntry Entry(string pst, string mailbox, TargetArchiveId identity, long size) =>
        new($"/src/{pst}", pst, new ArchiveRef(mailbox, identity), size, 1);

    // ---- B1: unidade decimal (100 GB, não 100 GiB) ----

    [Fact]
    public void LimitIsOneHundredGigabytesDecimal() =>
        Assert.Equal(100_000_000_000L, CapacityRule.OneHundredGigabytesInBytes);

    [Fact]
    public void ExactlyOneHundredGbIsWithinLimit() =>
        Assert.Equal(CapacityAssessmentResult.WithinLimit, CapacityRule.Evaluate(100_000_000_000L));

    [Fact]
    public void JustBelowLimitIsWithinLimit() =>
        Assert.Equal(CapacityAssessmentResult.WithinLimit, CapacityRule.Evaluate(99_999_999_999L));

    [Fact]
    public void JustAboveLimitRequiresAssessment() =>
        Assert.Equal(CapacityAssessmentResult.AssessmentRequired, CapacityRule.Evaluate(100_000_000_001L));

    [Fact]
    public void ValueBetweenDecimalGbAndBinaryGibIsBlocked()
    {
        // 100 GiB (107.374.182.400) está acima de 100 GB decimal e deve bloquear.
        Assert.True(OneHundredGibibytes > Limit);
        Assert.Equal(CapacityAssessmentResult.AssessmentRequired, CapacityRule.Evaluate(OneHundredGibibytes));
    }

    [Fact]
    public void CodeForAssessmentRequiredIsMicrosoftAssessmentRequired() =>
        Assert.Equal("MICROSOFT_ASSESSMENT_REQUIRED", CapacityRule.CodeFor(CapacityAssessmentResult.AssessmentRequired));

    // ---- Agrupamento por archive ----

    [Fact]
    public void SingleArchiveOverLimitIsBlocked()
    {
        var report = CapacityPlanner.Assess(new WaveSelection([Entry("a.pst", "shared@contoso.com", Limit + 1)]));
        Assert.True(report.AssessmentRequired);
        Assert.Single(report.PerArchive);
        Assert.Equal("MICROSOFT_ASSESSMENT_REQUIRED", report.PerArchive[0].RuleCode);
    }

    [Fact]
    public void ArtificialSplitAcrossManyEntriesDoesNotBypassTheRule()
    {
        var third = (Limit / 3) + 1;
        var report = CapacityPlanner.Assess(new WaveSelection(
        [
            Entry("a.pst", "shared@contoso.com", third),
            Entry("b.pst", "shared@contoso.com", third),
            Entry("c.pst", "shared@contoso.com", third),
        ]));

        Assert.True(report.AssessmentRequired);
        Assert.Single(report.PerArchive);
        Assert.Equal(3 * third, report.PerArchive[0].TotalBytes);
    }

    [Fact]
    public void MultipleArchivesAreEvaluatedIndependently()
    {
        var report = CapacityPlanner.Assess(new WaveSelection(
        [
            Entry("a.pst", "over@contoso.com", Limit + 1),
            Entry("b.pst", "under@contoso.com", 10),
        ]));

        Assert.True(report.AssessmentRequired);
        Assert.Equal(2, report.PerArchive.Count);
        var over = report.PerArchive.Single(a => a.Archive == new TargetArchiveId("over@contoso.com"));
        var under = report.PerArchive.Single(a => a.Archive == new TargetArchiveId("under@contoso.com"));
        Assert.Equal(CapacityAssessmentResult.AssessmentRequired, over.Result);
        Assert.Equal(CapacityAssessmentResult.WithinLimit, under.Result);
    }

    [Fact]
    public void AllArchivesUnderLimitIsNotBlocked()
    {
        var report = CapacityPlanner.Assess(new WaveSelection(
        [
            Entry("a.pst", "one@contoso.com", Limit),
            Entry("b.pst", "two@contoso.com", 5),
        ]));
        Assert.False(report.AssessmentRequired);
    }

    // ---- B2: identidade canônica (casing / alias / overflow) ----

    [Fact]
    public void CasingVariationsAreTheSameArchiveAndCannotBypassTheLimit()
    {
        // Cada entrada está abaixo do limite, mas a mesma identidade canônica soma acima dele.
        var half = (Limit / 2) + 1;
        var report = CapacityPlanner.Assess(new WaveSelection(
        [
            Entry("a.pst", "User@contoso.com", half),
            Entry("b.pst", "user@contoso.com", half),
        ]));

        Assert.Single(report.PerArchive);
        Assert.True(report.AssessmentRequired);
    }

    [Fact]
    public void ExplicitlyResolvedAliasesAreGroupedAsOneArchive()
    {
        var identity = new TargetArchiveId("archive-guid-123");
        var half = (Limit / 2) + 1;
        var report = CapacityPlanner.Assess(new WaveSelection(
        [
            Entry("a.pst", "primary@contoso.com", identity, half),
            Entry("b.pst", "alias@contoso.com", identity, half),
        ]));

        Assert.Single(report.PerArchive);
        Assert.True(report.AssessmentRequired);
    }

    [Fact]
    public void OverflowInArchiveSumFailsClosed()
    {
        var selection = new WaveSelection(
        [
            Entry("a.pst", "shared@contoso.com", long.MaxValue),
            Entry("b.pst", "shared@contoso.com", long.MaxValue),
        ]);
        Assert.Throws<OverflowException>(() => CapacityPlanner.Assess(selection));
    }
}
