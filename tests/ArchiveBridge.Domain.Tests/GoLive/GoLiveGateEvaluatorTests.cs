using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.ProductionReadiness;
using Xunit;

namespace ArchiveBridge.Domain.Tests.GoLive;

/// <summary>
/// AB-I8-010 — <see cref="GoLiveGateEvaluator"/>: fail-closed puro (nunca fabrica Pass para controle
/// ausente/incoerente), e <see cref="GoLiveOutcome.GoLiveAuthorized"/> só é alcançável quando o canário é
/// <see cref="CanaryOutcome.CanaryPassed"/>, o Production Readiness Review vigente coincide exatamente com o
/// vinculado pelo canário, E todos os controles Operations/Microsoft365 estão Pass.
/// </summary>
public sealed class GoLiveGateEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));
    private static readonly Sha256Hash OtherFingerprint = new(new string('b', 64));

    [Fact]
    public void CanaryNotPassedBlocksEvenWithEverythingElseSatisfied()
    {
        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.NotPassed, 3, SomeFingerprint, 3, SomeFingerprint, AllOperationalControlsPassing(), Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Code == GoLiveBlocker.CanaryNotPassedCode);
    }

    [Fact]
    public void NoCurrentReadinessReviewBlocksAsDrift()
    {
        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, currentReadinessReviewVersion: null,
            currentReadinessReviewFingerprint: null, AllOperationalControlsPassing(), Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Code == GoLiveBlocker.ReadinessReviewDriftCode);
    }

    [Fact]
    public void ADifferentCurrentReadinessReviewFingerprintBlocksAsDrift()
    {
        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, 3, OtherFingerprint, AllOperationalControlsPassing(), Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Code == GoLiveBlocker.ReadinessReviewDriftCode);
    }

    [Fact]
    public void ANewerCurrentReadinessReviewVersionBlocksAsDriftEvenWithTheSameFingerprintValue()
    {
        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, 4, SomeFingerprint, AllOperationalControlsPassing(), Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Code == GoLiveBlocker.ReadinessReviewDriftCode);
    }

    [Fact]
    public void AnEmptyOperationalResolvedDictionaryProducesABlockerForEveryOperationalControl()
    {
        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, 3, SomeFingerprint,
            new Dictionary<ReadinessControlId, ReadinessControlResult>(), Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Equal(GoLiveGateEvaluator.OperationalControls.Count, evaluation.OperationalControlResults.Count);
        Assert.All(evaluation.OperationalControlResults, result => Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status));
    }

    [Fact]
    public void WhenEverythingIsSatisfiedTheOutcomeIsGoLiveAuthorized()
    {
        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, 3, SomeFingerprint, AllOperationalControlsPassing(), Now);

        Assert.Equal(GoLiveOutcome.GoLiveAuthorized, evaluation.Outcome);
        Assert.Empty(evaluation.Blockers);
        Assert.Equal(GoLiveGateEvaluator.OperationalControls.Count, evaluation.OperationalControlResults.Count);
        Assert.All(evaluation.OperationalControlResults, result => Assert.Equal(ReadinessControlStatus.Pass, result.Status));
    }

    [Fact]
    public void ASingleNonPassOperationalControlBlocksEvenWhenAllOthersPass()
    {
        var resolved = AllOperationalControlsPassing();
        var blockedControlId = new ReadinessControlId("OPS.RPO_EXERCISED");
        resolved[blockedControlId] = ReadinessControlResult.Create(
            blockedControlId, ReadinessGateGroup.Operations, ReadinessControlStatus.Blocked,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "recovery-readiness:rpo"), "RPO_OBJECTIVE_BLOCKED", Now);

        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, 3, SomeFingerprint, resolved, Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Code == $"{GoLiveBlocker.OperationalControlNotPassCode}:{blockedControlId.Value}");
    }

    [Fact]
    public void AControlResultUnderTheWrongKeyIsTreatedAsMissingRatherThanTrusted()
    {
        var resolved = AllOperationalControlsPassing();
        var correctId = new ReadinessControlId("OPS.RPO_EXERCISED");
        var wrongId = new ReadinessControlId("OPS.RTO_EXERCISED");

        resolved[correctId] = ReadinessControlResult.Create(
            wrongId, ReadinessGateGroup.Operations, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "tampered"), reasonCode: string.Empty, Now);

        var evaluation = GoLiveGateEvaluator.Evaluate(
            CanaryOutcome.CanaryPassed, 3, SomeFingerprint, 3, SomeFingerprint, resolved, Now);

        Assert.Equal(GoLiveOutcome.Blocked, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.Code == $"{GoLiveBlocker.OperationalControlNotPassCode}:{correctId.Value}");
    }

    [Fact]
    public void OnlyOperationsAndMicrosoft365GroupsAreRevalidated()
    {
        Assert.All(GoLiveGateEvaluator.OperationalControls, definition =>
            Assert.True(definition.Group is ReadinessGateGroup.Operations or ReadinessGateGroup.Microsoft365));
    }

    private static Dictionary<ReadinessControlId, ReadinessControlResult> AllOperationalControlsPassing()
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in GoLiveGateEvaluator.OperationalControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
