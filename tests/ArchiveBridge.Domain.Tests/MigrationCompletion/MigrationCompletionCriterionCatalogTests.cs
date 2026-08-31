using ArchiveBridge.Domain.MigrationCompletion;
using Xunit;

namespace ArchiveBridge.Domain.Tests.MigrationCompletion;

/// <summary>
/// AB-I8-010/AB-I8-011 — <see cref="MigrationCompletionCriterionCatalog"/>: identidade estável, cobertura fixa
/// dos onze critérios do §49, e a classificação SystemDerived/EvidenceDerived/HumanApproval corrigida por
/// AB-I8-011 (nenhum critério tecnicamente objetivo pode permanecer mascarado sob um "Attested" genérico).
/// </summary>
public sealed class MigrationCompletionCriterionCatalogTests
{
    [Fact]
    public void AllCriteriaHaveUniqueStableIds()
    {
        var ids = MigrationCompletionCriterionCatalog.AllCriteria.Select(definition => definition.Id.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TotalCriterionCountMatchesTheEvenRunbookBulletCount()
    {
        Assert.Equal(11, MigrationCompletionCriterionCatalog.AllCriteria.Count);
    }

    [Fact]
    public void DefinitionThrowsForAnUnknownCriterion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MigrationCompletionCriterionCatalog.Definition(new MigrationCompletionCriterionId("COMPLETION.NOT_A_REAL_CRITERION")));
    }

    [Fact]
    public void IsKnownIsFalseForAnUnknownCriterion()
    {
        Assert.False(MigrationCompletionCriterionCatalog.IsKnown(new MigrationCompletionCriterionId("COMPLETION.NOT_A_REAL_CRITERION")));
    }

    // Únicos dois critérios resolvidos automaticamente a partir de um store canônico REAL e SUFICIENTE já
    // aceito (reconciliation certificate / service result report do I6) — inalterados por AB-I8-011.
    [Theory]
    [InlineData("COMPLETION.RECONCILIATION_CLOSED")]
    [InlineData("COMPLETION.PROVIDER_RESULTS_COLLECTED")]
    public void TheTwoSystemDerivedCriteriaAreClassifiedCorrectly(string criterionId)
    {
        var definition = MigrationCompletionCriterionCatalog.Definition(new MigrationCompletionCriterionId(criterionId));
        Assert.Equal(MigrationCompletionCriterionEvidenceSource.SystemDerived, definition.EvidenceSource);
    }

    // AB-I8-011: critérios tecnicamente/objetivamente verificáveis, mas para os quais este repositório NÃO
    // expõe hoje um store canônico suficiente — nunca satisfeitos por atestação humana, permanentemente
    // NotMeasured até que um store real seja implementado.
    [Theory]
    [InlineData("COMPLETION.SOURCE_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.PARTS_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM")]
    [InlineData("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL")]
    public void TheFourEvidenceDerivedCriteriaAreClassifiedCorrectly(string criterionId)
    {
        var definition = MigrationCompletionCriterionCatalog.Definition(new MigrationCompletionCriterionId(criterionId));
        Assert.Equal(MigrationCompletionCriterionEvidenceSource.EvidenceDerived, definition.EvidenceSource);
    }

    // Genuinamente processuais/de decisão humana — sem verdade técnica automatizável neste repositório (nem
    // faria sentido conceitual que houvesse).
    [Theory]
    [InlineData("COMPLETION.SCOPE_AND_POLICY_SIGNED")]
    [InlineData("COMPLETION.HOLDS_RETENTION_REVIEWED")]
    [InlineData("COMPLETION.USERS_INACTIVE_HANDLED")]
    [InlineData("COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED")]
    [InlineData("COMPLETION.CUSTOMER_FINAL_APPROVAL")]
    public void TheFiveHumanApprovalCriteriaAreClassifiedCorrectly(string criterionId)
    {
        var definition = MigrationCompletionCriterionCatalog.Definition(new MigrationCompletionCriterionId(criterionId));
        Assert.Equal(MigrationCompletionCriterionEvidenceSource.HumanApproval, definition.EvidenceSource);
    }

    [Fact]
    public void EveryCriterionIsClassifiedAsExactlyOneOfTheThreeKnownEvidenceSources()
    {
        Assert.All(MigrationCompletionCriterionCatalog.AllCriteria, definition => Assert.True(
            definition.EvidenceSource is MigrationCompletionCriterionEvidenceSource.SystemDerived
                or MigrationCompletionCriterionEvidenceSource.EvidenceDerived
                or MigrationCompletionCriterionEvidenceSource.HumanApproval));
    }

    [Fact]
    public void TheThreeEvidenceSourceClassesPartitionAllElevenCriteriaWithoutOverlap()
    {
        var byClass = MigrationCompletionCriterionCatalog.AllCriteria
            .GroupBy(definition => definition.EvidenceSource)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(2, byClass.GetValueOrDefault(MigrationCompletionCriterionEvidenceSource.SystemDerived));
        Assert.Equal(4, byClass.GetValueOrDefault(MigrationCompletionCriterionEvidenceSource.EvidenceDerived));
        Assert.Equal(5, byClass.GetValueOrDefault(MigrationCompletionCriterionEvidenceSource.HumanApproval));
    }
}
