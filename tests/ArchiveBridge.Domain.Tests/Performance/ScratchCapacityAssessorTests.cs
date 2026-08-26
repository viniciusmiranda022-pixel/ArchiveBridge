using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §4 — Unknown de capacidade NUNCA vira Enough por default.</summary>
public sealed class ScratchCapacityAssessorTests
{
    [Fact]
    public void NullAvailableCapacityIsAlwaysUnknownNeverEnough()
    {
        var assessment = ScratchCapacityAssessor.Assess(requiredScratchBytes: 100, availableScratchBytes: null);

        Assert.Equal(CapacityBudgetOutcome.Unknown, assessment.Outcome);
        Assert.NotEqual(CapacityBudgetOutcome.Enough, assessment.Outcome);
        Assert.Null(assessment.AvailableScratchBytes);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
    }

    [Fact]
    public void NegativeAvailableCapacityIsTreatedAsUnknownNeverEnough()
    {
        var assessment = ScratchCapacityAssessor.Assess(requiredScratchBytes: 100, availableScratchBytes: -1);

        Assert.Equal(CapacityBudgetOutcome.Unknown, assessment.Outcome);
    }

    [Fact]
    public void AvailableExactlyEqualToRequiredIsEnough()
    {
        var assessment = ScratchCapacityAssessor.Assess(requiredScratchBytes: 100, availableScratchBytes: 100);

        Assert.Equal(CapacityBudgetOutcome.Enough, assessment.Outcome);
    }

    [Fact]
    public void AvailableOneByteBelowRequiredIsInsufficient()
    {
        var assessment = ScratchCapacityAssessor.Assess(requiredScratchBytes: 100, availableScratchBytes: 99);

        Assert.Equal(CapacityBudgetOutcome.Insufficient, assessment.Outcome);
    }

    [Fact]
    public void AvailableAboveRequiredIsEnough()
    {
        var assessment = ScratchCapacityAssessor.Assess(requiredScratchBytes: 100, availableScratchBytes: 1_000);

        Assert.Equal(CapacityBudgetOutcome.Enough, assessment.Outcome);
    }

    [Fact]
    public void ZeroRequiredWithZeroAvailableIsEnough()
    {
        var assessment = ScratchCapacityAssessor.Assess(requiredScratchBytes: 0, availableScratchBytes: 0);

        Assert.Equal(CapacityBudgetOutcome.Enough, assessment.Outcome);
    }

    [Fact]
    public void NegativeRequiredThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScratchCapacityAssessor.Assess(requiredScratchBytes: -1, availableScratchBytes: 100));
    }

    [Fact]
    public void ReasonIsAlwaysPresentRegardlessOfOutcome()
    {
        foreach (var assessment in new[]
        {
            ScratchCapacityAssessor.Assess(10, null),
            ScratchCapacityAssessor.Assess(10, -5),
            ScratchCapacityAssessor.Assess(10, 10),
            ScratchCapacityAssessor.Assess(10, 5),
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
        }
    }
}
