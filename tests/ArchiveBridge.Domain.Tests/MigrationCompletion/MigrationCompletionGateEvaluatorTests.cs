using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using Xunit;

namespace ArchiveBridge.Domain.Tests.MigrationCompletion;

/// <summary>
/// AB-I8-010 — <see cref="MigrationCompletionGateEvaluator"/>: fail-closed puro; <see cref="MigrationCompletionOutcome.Eligible"/>
/// só é alcançável quando TODOS os onze critérios do §49 estão Pass; cada critério ausente individualmente
/// impede Eligible (escopo obrigatório item 13).
/// </summary>
public sealed class MigrationCompletionGateEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    [Fact]
    public void AnEmptyResolvedDictionaryProducesBlockedWithEveryCriterionAsABlocker()
    {
        var evaluation = MigrationCompletionGateEvaluator.Evaluate(new Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult>(), Now);

        Assert.Equal(MigrationCompletionOutcome.Blocked, evaluation.Outcome);
        Assert.Equal(MigrationCompletionCriterionCatalog.AllCriteria.Count, evaluation.Blockers.Count);
        Assert.Equal(MigrationCompletionCriterionCatalog.AllCriteria.Count, evaluation.CriterionResults.Count);
        Assert.All(evaluation.CriterionResults, result => Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status));
    }

    [Fact]
    public void WhenEveryCatalogCriterionIsResolvedAsPassTheOutcomeIsEligible()
    {
        var resolved = AllCriteriaPassing();

        var evaluation = MigrationCompletionGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(MigrationCompletionOutcome.Eligible, evaluation.Outcome);
        Assert.Empty(evaluation.Blockers);
        Assert.Equal(11, MigrationCompletionCriterionCatalog.AllCriteria.Count);
    }

    [Theory]
    [InlineData("COMPLETION.SCOPE_AND_POLICY_SIGNED")]
    [InlineData("COMPLETION.SOURCE_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.PARTS_DISPOSITION_COMPLETE")]
    [InlineData("COMPLETION.PROVIDER_RESULTS_COLLECTED")]
    [InlineData("COMPLETION.RECONCILIATION_CLOSED")]
    [InlineData("COMPLETION.HOLDS_RETENTION_REVIEWED")]
    [InlineData("COMPLETION.USERS_INACTIVE_HANDLED")]
    [InlineData("COMPLETION.EVIDENCE_PACKAGE_PUBLISHED_WORM")]
    [InlineData("COMPLETION.ROLLBACK_DECOMMISSION_WINDOW_DEFINED")]
    [InlineData("COMPLETION.CUSTOMER_FINAL_APPROVAL")]
    [InlineData("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL")]
    public void EachIndividualCriterionMissingIndividuallyBlocksEligibility(string criterionIdValue)
    {
        var resolved = AllCriteriaPassing();
        var missingId = new MigrationCompletionCriterionId(criterionIdValue);
        resolved.Remove(missingId);

        var evaluation = MigrationCompletionGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(MigrationCompletionOutcome.Blocked, evaluation.Outcome);
        var blocker = Assert.Single(evaluation.Blockers);
        Assert.Equal(missingId, blocker.CriterionId);
        Assert.Equal(ReadinessControlStatus.NotMeasured, blocker.Status);
    }

    [Fact]
    public void ACriterionResultUnderTheWrongKeyIsTreatedAsMissingRatherThanTrusted()
    {
        var resolved = AllCriteriaPassing();
        var correctId = new MigrationCompletionCriterionId("COMPLETION.CUSTOMER_FINAL_APPROVAL");
        var wrongId = new MigrationCompletionCriterionId("COMPLETION.NO_ACTIVE_TEMPORARY_CREDENTIAL");

        resolved[correctId] = MigrationCompletionCriterionResult.Create(
            wrongId, ReadinessControlStatus.Pass, ReadinessEvidenceReference.Attested(SomeFingerprint, "tampered"),
            reasonCode: string.Empty, Now);

        var evaluation = MigrationCompletionGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(MigrationCompletionOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.CriterionId == correctId && blocker.ReasonCode == "CRITERION_RESULT_MISMATCH");
    }

    [Fact]
    public void ReconciliationFailBlocksEvenWithAllOtherCriteriaPassing()
    {
        var resolved = AllCriteriaPassing();
        var reconciliationId = new MigrationCompletionCriterionId("COMPLETION.RECONCILIATION_CLOSED");
        resolved[reconciliationId] = MigrationCompletionCriterionResult.Create(
            reconciliationId, ReadinessControlStatus.Fail,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "reconciliation-certificate:v1"), "RECONCILIATION_NOT_CLOSED", Now);

        var evaluation = MigrationCompletionGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(MigrationCompletionOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.CriterionId == reconciliationId);
    }

    private static Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> AllCriteriaPassing()
    {
        var resolved = new Dictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult>();
        foreach (var definition in MigrationCompletionCriterionCatalog.AllCriteria)
        {
            resolved[definition.Id] = MigrationCompletionCriterionResult.Create(
                definition.Id, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
