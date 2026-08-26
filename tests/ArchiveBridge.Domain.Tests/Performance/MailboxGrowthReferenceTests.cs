using ArchiveBridge.Domain.Performance;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §5, acceptance criterion 5 — ~24 GB/dia/mailbox permanece típico/referência, nunca SLA.</summary>
public sealed class MailboxGrowthReferenceTests
{
    [Fact]
    public void TypicalRateIsExactlyTwentyFourGigabytesDecimal()
    {
        Assert.Equal(24_000_000_000L, MailboxGrowthReference.TypicalBytesPerMailboxPerDay);
    }

    [Fact]
    public void AsReferenceEstimateNeverProducesAContractualSla()
    {
        var estimate = MailboxGrowthReference.AsReferenceEstimate();

        Assert.Equal(MailboxGrowthReference.MetricName, estimate.MetricName);
        Assert.Equal(MailboxGrowthReference.TypicalBytesPerMailboxPerDay, estimate.Value);
        Assert.Equal("bytes/day", estimate.Unit);
        Assert.Equal(MailboxGrowthReference.SourceCitation, estimate.SourceCitation);
    }

    [Fact]
    public void SourceCitationExplicitlyDisclaimsSlaStatus()
    {
        Assert.Contains("não SLA", MailboxGrowthReference.SourceCitation, StringComparison.Ordinal);
        Assert.Contains("runbook", MailboxGrowthReference.SourceCitation, StringComparison.OrdinalIgnoreCase);
    }
}
