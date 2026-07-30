using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Tests;

public sealed class Slice2CapacityTests
{
    private const long Limit = CapacityRule.HundredGigabytesInBytes;

    private static WaveEntry Entry(string pst, string mailbox, long size) =>
        new($"/src/{pst}", pst, new ArchiveRef(mailbox), size, 1);

    [Fact]
    public void ExactlyHundredGibIsWithinLimit() =>
        Assert.Equal(CapacityAssessmentResult.WithinLimit, CapacityRule.Evaluate(Limit));

    [Fact]
    public void JustBelowLimitIsWithinLimit() =>
        Assert.Equal(CapacityAssessmentResult.WithinLimit, CapacityRule.Evaluate(Limit - 1));

    [Fact]
    public void JustAboveLimitRequiresAssessment() =>
        Assert.Equal(CapacityAssessmentResult.AssessmentRequired, CapacityRule.Evaluate(Limit + 1));

    [Fact]
    public void CodeForAssessmentRequiredIsMicrosoftAssessmentRequired() =>
        Assert.Equal("MICROSOFT_ASSESSMENT_REQUIRED", CapacityRule.CodeFor(CapacityAssessmentResult.AssessmentRequired));

    [Fact]
    public void SingleArchiveOverLimitIsBlocked()
    {
        var selection = new WaveSelection([Entry("a.pst", "shared@contoso.com", Limit + 1)]);
        var report = CapacityPlanner.Assess(selection);
        Assert.True(report.AssessmentRequired);
        Assert.Single(report.PerArchive);
        Assert.Equal("MICROSOFT_ASSESSMENT_REQUIRED", report.PerArchive[0].RuleCode);
    }

    [Fact]
    public void ArtificialSplitAcrossManyEntriesDoesNotBypassTheRule()
    {
        // Três entradas do MESMO archive, cada uma abaixo do limite, mas somando acima dele.
        var third = (Limit / 3) + 1;
        var selection = new WaveSelection(
        [
            Entry("a.pst", "shared@contoso.com", third),
            Entry("b.pst", "shared@contoso.com", third),
            Entry("c.pst", "shared@contoso.com", third),
        ]);
        var report = CapacityPlanner.Assess(selection);

        Assert.True(report.AssessmentRequired);
        Assert.Single(report.PerArchive);
        Assert.Equal(3 * third, report.PerArchive[0].TotalBytes);
    }

    [Fact]
    public void MultipleArchivesAreEvaluatedIndependently()
    {
        var selection = new WaveSelection(
        [
            Entry("a.pst", "over@contoso.com", Limit + 1),
            Entry("b.pst", "under@contoso.com", 10),
        ]);
        var report = CapacityPlanner.Assess(selection);

        Assert.True(report.AssessmentRequired);
        Assert.Equal(2, report.PerArchive.Count);
        var over = report.PerArchive.Single(a => a.Mailbox == "over@contoso.com");
        var under = report.PerArchive.Single(a => a.Mailbox == "under@contoso.com");
        Assert.Equal(CapacityAssessmentResult.AssessmentRequired, over.Result);
        Assert.Equal(CapacityAssessmentResult.WithinLimit, under.Result);
    }

    [Fact]
    public void AllArchivesUnderLimitIsNotBlocked()
    {
        var selection = new WaveSelection(
        [
            Entry("a.pst", "one@contoso.com", Limit),
            Entry("b.pst", "two@contoso.com", 5),
        ]);
        var report = CapacityPlanner.Assess(selection);
        Assert.False(report.AssessmentRequired);
    }
}
