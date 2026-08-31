using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Canary;

/// <summary>
/// AB-I8-004 — <see cref="CanaryGateEvaluator"/>: fail-closed puro (nunca fabrica Pass para cenário ausente/
/// incoerente), e <see cref="CanaryOutcome.CanaryPassed"/> só é alcançável quando TODOS os 10 cenários do
/// catálogo estão Pass.
/// </summary>
public sealed class CanaryGateEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    [Fact]
    public void AnEmptyResolvedDictionaryProducesNotPassedWithEveryScenarioAsABlocker()
    {
        var evaluation = CanaryGateEvaluator.Evaluate(new Dictionary<CanaryScenarioId, CanaryScenarioResult>(), Now);

        Assert.Equal(CanaryOutcome.NotPassed, evaluation.Outcome);
        Assert.Equal(CanaryScenarioCatalog.AllScenarios.Count, evaluation.Blockers.Count);
        Assert.Equal(CanaryScenarioCatalog.AllScenarios.Count, evaluation.ScenarioResults.Count);
        Assert.All(evaluation.ScenarioResults, result => Assert.Equal(CanaryScenarioStatus.Pending, result.Status));
        Assert.All(evaluation.Blockers, blocker => Assert.Equal("SCENARIO_EVIDENCE_MISSING", blocker.ReasonCode));
    }

    [Fact]
    public void WhenEveryCatalogScenarioIsResolvedAsPassTheOutcomeIsCanaryPassed()
    {
        var resolved = AllScenariosPassing();

        var evaluation = CanaryGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(CanaryOutcome.CanaryPassed, evaluation.Outcome);
        Assert.Empty(evaluation.Blockers);
        Assert.Equal(CanaryScenarioCatalog.AllScenarios.Count, evaluation.ScenarioResults.Count);
        Assert.All(evaluation.ScenarioResults, result => Assert.Equal(CanaryScenarioStatus.Pass, result.Status));
    }

    [Fact]
    public void ASingleNonPassScenarioKeepsTheOutcomeNotPassedEvenWhenAllOthersPass()
    {
        var resolved = AllScenariosPassing();
        var blockedScenarioId = new CanaryScenarioId("CANARY.CRASH_RECOVERY");
        resolved[blockedScenarioId] = CanaryScenarioResult.Create(
            blockedScenarioId, CanaryScenarioStatus.NotPerformed,
            CanaryEvidenceReference.SystemDerived(SomeFingerprint, "recovery-readiness:v1"), "CRASH_RECOVERY_NOT_EXERCISED", Now);

        var evaluation = CanaryGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(CanaryOutcome.NotPassed, evaluation.Outcome);
        var blocker = Assert.Single(evaluation.Blockers);
        Assert.Equal(blockedScenarioId, blocker.ScenarioId);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, blocker.Status);
    }

    [Fact]
    public void AScenarioResultUnderTheWrongKeyIsTreatedAsMissingRatherThanTrusted()
    {
        var resolved = AllScenariosPassing();
        var correctId = new CanaryScenarioId("CANARY.CRASH_RECOVERY");
        var wrongId = new CanaryScenarioId("CANARY.RESTORE_ROLLBACK_OPERATIONAL");

        // Constrói um resultado Pass para o cenário errado, sob a chave do cenário certo — o avaliador nunca
        // confia cegamente nisso.
        resolved[correctId] = CanaryScenarioResult.Create(
            wrongId, CanaryScenarioStatus.Pass, CanaryEvidenceReference.SystemDerived(SomeFingerprint, "tampered"),
            reasonCode: string.Empty, Now);

        var evaluation = CanaryGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(CanaryOutcome.NotPassed, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.ScenarioId == correctId && blocker.ReasonCode == "SCENARIO_RESULT_MISMATCH");
    }

    [Fact]
    public void ScenarioResultsArePreservedInTheDeterministicCatalogOrder()
    {
        var evaluation = CanaryGateEvaluator.Evaluate(new Dictionary<CanaryScenarioId, CanaryScenarioResult>(), Now);

        var expectedOrder = CanaryScenarioCatalog.AllScenarios.Select(definition => definition.Id).ToList();
        var actualOrder = evaluation.ScenarioResults.Select(result => result.ScenarioId).ToList();
        Assert.Equal(expectedOrder, actualOrder);
    }

    [Fact]
    public void EvaluatingTheSameInputTwiceIsDeterministic()
    {
        var resolved = AllScenariosPassing();

        var first = CanaryGateEvaluator.Evaluate(resolved, Now);
        var second = CanaryGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Blockers.Count, second.Blockers.Count);
        Assert.Equal(
            first.ScenarioResults.Select(r => (r.ScenarioId, r.Status)),
            second.ScenarioResults.Select(r => (r.ScenarioId, r.Status)));
    }

    [Fact]
    public void RunningIsNeverTreatedAsPass()
    {
        var resolved = AllScenariosPassing();
        var runningId = new CanaryScenarioId("CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT");
        resolved[runningId] = CanaryScenarioResult.Create(
            runningId, CanaryScenarioStatus.Running, CanaryEvidenceReference.OperatorAttested(SomeFingerprint, "in-progress"),
            reasonCode: string.Empty, Now);

        var evaluation = CanaryGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(CanaryOutcome.NotPassed, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.ScenarioId == runningId && blocker.Status == CanaryScenarioStatus.Running);
    }

    private static Dictionary<CanaryScenarioId, CanaryScenarioResult> AllScenariosPassing()
    {
        var resolved = new Dictionary<CanaryScenarioId, CanaryScenarioResult>();
        foreach (var definition in CanaryScenarioCatalog.AllScenarios)
        {
            resolved[definition.Id] = CanaryScenarioResult.Create(
                definition.Id, CanaryScenarioStatus.Pass,
                CanaryEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
