using ArchiveBridge.Domain.ProductionReadiness;
using Xunit;

namespace ArchiveBridge.Domain.Tests.ProductionReadiness;

/// <summary>AB-I8-001 — <see cref="ProductionReadinessPolicyInvariants"/>: os dois controles M365 resolvidos por auto-checagem pura de código vivo.</summary>
public sealed class ProductionReadinessPolicyInvariantsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluateReturnsExactlyTheTwoM365PolicyControls()
    {
        var results = ProductionReadinessPolicyInvariants.Evaluate(Now);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.ControlId == new ReadinessControlId("M365.IMPORT_LIMITS_100GB_500ROWS"));
        Assert.Contains(results, r => r.ControlId == new ReadinessControlId("M365.TARGET_ROOT_POLICY"));
        Assert.All(results, r => Assert.Equal(ReadinessGateGroup.Microsoft365, r.Group));
        Assert.All(results, r => Assert.Equal(ReadinessEvidenceKind.SystemDerived, r.Evidence.Kind));
    }

    [Fact]
    public void TheDocumentedImportLimitsAreCurrentlySatisfiedByTheConfiguredPolicy()
    {
        var results = ProductionReadinessPolicyInvariants.Evaluate(Now);
        var importLimits = results.Single(r => r.ControlId == new ReadinessControlId("M365.IMPORT_LIMITS_100GB_500ROWS"));

        Assert.Equal(ReadinessControlStatus.Pass, importLimits.Status);
    }

    [Fact]
    public void TargetRootPolicyIsCurrentlySatisfiedByTheDomainInvariant()
    {
        var results = ProductionReadinessPolicyInvariants.Evaluate(Now);
        var targetRootPolicy = results.Single(r => r.ControlId == new ReadinessControlId("M365.TARGET_ROOT_POLICY"));

        Assert.Equal(ReadinessControlStatus.Pass, targetRootPolicy.Status);
    }

    [Fact]
    public void EvaluatingTwiceIsDeterministic()
    {
        var first = ProductionReadinessPolicyInvariants.Evaluate(Now);
        var second = ProductionReadinessPolicyInvariants.Evaluate(Now);

        Assert.Equal(
            first.Select(r => (r.ControlId, r.Status, r.Evidence.Fingerprint)),
            second.Select(r => (r.ControlId, r.Status, r.Evidence.Fingerprint)));
    }
}
