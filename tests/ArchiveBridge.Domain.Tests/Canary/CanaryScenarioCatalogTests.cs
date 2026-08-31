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

    /// <summary>
    /// AB-I8-006: apenas CANARY.CORPUS_ITEM_TYPE_DIVERSITY permanece OperatorAttested — os outros quatro
    /// cenários anteriormente OperatorAttested (PST_SIZE_BOUNDARY_COVERAGE, REPLAY_SAME_TARGET_ROOT_IDEMPOTENT,
    /// DIFFERENT_TARGET_ROOT_BLOCKS, KNOWN_CORRUPTION_QUARANTINE) foram reclassificados para SystemDerived
    /// porque este repositório já tem evidência canônica capaz de prová-los server-side (I8-006, engenharia
    /// reviewer: nenhum controle tecnicamente-verificável pode continuar aceitando Pass a partir de status/
    /// texto alegado pelo operador).
    /// </summary>
    [Fact]
    public void ExactlyOneScenarioIsOperatorAttestedAndItIsCorpusItemTypeDiversity()
    {
        var operatorAttestedScenarios = CanaryScenarioCatalog.AllScenarios
            .Where(definition => definition.EvidenceSource == CanaryScenarioEvidenceSource.OperatorAttested)
            .ToList();

        var operatorAttested = Assert.Single(operatorAttestedScenarios);
        Assert.Equal("CANARY.CORPUS_ITEM_TYPE_DIVERSITY", operatorAttested.Id.Value);
    }

    [Theory]
    [InlineData("CANARY.PST_SIZE_BOUNDARY_COVERAGE")]
    [InlineData("CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT")]
    [InlineData("CANARY.DIFFERENT_TARGET_ROOT_BLOCKS")]
    [InlineData("CANARY.KNOWN_CORRUPTION_QUARANTINE")]
    public void TheFourReclassifiedScenariosAreSystemDerived(string scenarioIdValue)
    {
        var definition = CanaryScenarioCatalog.Definition(new CanaryScenarioId(scenarioIdValue));

        Assert.Equal(CanaryScenarioEvidenceSource.SystemDerived, definition.EvidenceSource);
    }

    [Theory]
    [InlineData("CANARY.PST_SIZE_BOUNDARY_COVERAGE")]
    [InlineData("CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT")]
    [InlineData("CANARY.DIFFERENT_TARGET_ROOT_BLOCKS")]
    [InlineData("CANARY.KNOWN_CORRUPTION_QUARANTINE")]
    public void RequireOperatorAttestableRejectsEachReclassifiedScenario(string scenarioIdValue)
    {
        Assert.Throws<CanaryScenarioNotAttestableException>(
            () => CanaryScenarioCatalog.RequireOperatorAttestable(new CanaryScenarioId(scenarioIdValue)));
    }

    [Fact]
    public void EightScenariosAreSystemDerived()
    {
        var systemDerivedCount = CanaryScenarioCatalog.AllScenarios
            .Count(definition => definition.EvidenceSource == CanaryScenarioEvidenceSource.SystemDerived);

        Assert.Equal(8, systemDerivedCount);
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
