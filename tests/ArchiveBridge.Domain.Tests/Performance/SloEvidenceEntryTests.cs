using ArchiveBridge.Domain.Performance.SloEvidence;

namespace ArchiveBridge.Domain.Tests.Performance;

/// <summary>AB-I7-003 §5 — a matriz de evidência nunca mistura um GateStatus com um payload incompatível.</summary>
public sealed class SloEvidenceEntryTests
{
    private static readonly ObservedMetric SampleObserved = new("Metric", 1.0, "ms", DateTimeOffset.UtcNow);

    [Fact]
    public void MeasuredWithObservedMetricSucceeds()
    {
        var entry = new SloEvidenceEntry("Metric", GateStatus.Measured, SampleObserved, reference: null, sla: null, blockedOrNotMeasuredReason: null);

        Assert.Equal(GateStatus.Measured, entry.Status);
        Assert.Same(SampleObserved, entry.Observed);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public void MeasuredWithoutObservedMetricThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            new SloEvidenceEntry("Metric", GateStatus.Measured, observed: null, reference: null, sla: null, blockedOrNotMeasuredReason: null));
    }

    [Theory]
    [InlineData(GateStatus.NotMeasured)]
    [InlineData(GateStatus.NotApplicable)]
    [InlineData(GateStatus.BlockedByExternalDependency)]
    public void AnyNonMeasuredStatusWithAnObservedMetricThrows(GateStatus status)
    {
        Assert.Throws<ArgumentException>(() =>
            new SloEvidenceEntry("Metric", status, SampleObserved, reference: null, sla: null, blockedOrNotMeasuredReason: "motivo"));
    }

    [Theory]
    [InlineData(GateStatus.NotMeasured)]
    [InlineData(GateStatus.BlockedByExternalDependency)]
    public void NotMeasuredOrBlockedWithoutAnExplicitReasonThrows(GateStatus status)
    {
        Assert.Throws<ArgumentException>(() =>
            new SloEvidenceEntry("Metric", status, observed: null, reference: null, sla: null, blockedOrNotMeasuredReason: null));
    }

    [Theory]
    [InlineData(GateStatus.NotMeasured)]
    [InlineData(GateStatus.BlockedByExternalDependency)]
    public void NotMeasuredOrBlockedWithAnExplicitReasonSucceeds(GateStatus status)
    {
        var entry = new SloEvidenceEntry("Metric", status, observed: null, reference: null, sla: null, blockedOrNotMeasuredReason: "ambiente indisponível");

        Assert.Equal(status, entry.Status);
        Assert.Null(entry.Observed);
        Assert.Equal("ambiente indisponível", entry.Reason);
    }

    [Fact]
    public void NotApplicableNeverRequiresAReason()
    {
        var entry = new SloEvidenceEntry("Metric", GateStatus.NotApplicable, observed: null, reference: null, sla: null, blockedOrNotMeasuredReason: null);

        Assert.Equal(GateStatus.NotApplicable, entry.Status);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public void ContractualSlaIsAlwaysNotConfiguredThroughItsOnlyPublicConstructor()
    {
        var sla = ContractualSla.NotConfigured("Metric");

        Assert.Equal("NOT_CONFIGURED", sla.Status);
        Assert.Null(sla.SourceCitation);
    }

    [Fact]
    public void ObservedMetricRejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentException>(() => new ObservedMetric("Metric", double.NaN, "ms", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new ObservedMetric("Metric", double.PositiveInfinity, "ms", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReferenceEstimateRequiresASourceCitation()
    {
        Assert.Throws<ArgumentException>(() => new ReferenceEstimate("Metric", 1.0, "ms", sourceCitation: string.Empty));
    }
}
