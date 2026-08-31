using ArchiveBridge.Domain.Canary;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Canary;

/// <summary>AB-I8-004 — <see cref="CanaryScenarioCatalog"/>: os dez cenários do runbook §48 estão materializados, e a classificação de evidência de cada um é fixa e nunca fornecida pelo chamador.</summary>
public sealed class CanaryScenarioCatalogTests
{
    [Fact]
    public void TheCatalogHasExactlyTenScenarios()
    {
        Assert.Equal(10, CanaryScenarioCatalog.AllScenarios.Count);
    }

    [Fact]
    public void AllScenarioIdsAreUnique()
    {
        var ids = CanaryScenarioCatalog.AllScenarios.Select(definition => definition.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void ExactlyOneScenarioIsTheApprovalGate()
    {
        var approvalScenarios = CanaryScenarioCatalog.AllScenarios
            .Where(definition => definition.EvidenceSource == CanaryScenarioEvidenceSource.ApprovalDecision)
            .ToList();

        var approval = Assert.Single(approvalScenarios);
        Assert.Equal(CanaryScenarioCatalog.FirstWaveApprovalScenarioId, approval.Id);
    }

    [Fact]
    public void DefinitionThrowsForAnUnknownScenarioId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CanaryScenarioCatalog.Definition(new CanaryScenarioId("CANARY.UNKNOWN_SCENARIO")));
    }

    [Fact]
    public void IsKnownIsFalseForAnUnknownScenarioId()
    {
        Assert.False(CanaryScenarioCatalog.IsKnown(new CanaryScenarioId("CANARY.UNKNOWN_SCENARIO")));
    }

    [Fact]
    public void RequireOperatorAttestableThrowsForTheApprovalGate()
    {
        Assert.Throws<CanaryScenarioNotAttestableException>(
            () => CanaryScenarioCatalog.RequireOperatorAttestable(CanaryScenarioCatalog.FirstWaveApprovalScenarioId));
    }

    [Fact]
    public void RequireOperatorAttestableThrowsForASystemDerivedScenario()
    {
        var systemDerivedScenario = CanaryScenarioCatalog.AllScenarios
            .First(definition => definition.EvidenceSource == CanaryScenarioEvidenceSource.SystemDerived);

        Assert.Throws<CanaryScenarioNotAttestableException>(() => CanaryScenarioCatalog.RequireOperatorAttestable(systemDerivedScenario.Id));
    }

    [Fact]
    public void RequireOperatorAttestableThrowsForAnUnknownScenario()
    {
        Assert.Throws<CanaryScenarioNotAttestableException>(
            () => CanaryScenarioCatalog.RequireOperatorAttestable(new CanaryScenarioId("CANARY.UNKNOWN_SCENARIO")));
    }

    [Fact]
    public void RequireOperatorAttestableSucceedsForAnOperatorAttestedScenario()
    {
        var operatorAttestedScenario = CanaryScenarioCatalog.AllScenarios
            .First(definition => definition.EvidenceSource == CanaryScenarioEvidenceSource.OperatorAttested);

        var definition = CanaryScenarioCatalog.RequireOperatorAttestable(operatorAttestedScenario.Id);

        Assert.Equal(operatorAttestedScenario.Id, definition.Id);
    }
}
