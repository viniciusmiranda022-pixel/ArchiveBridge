using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using Xunit;

namespace ArchiveBridge.Domain.Tests.ProductionReadiness;

/// <summary>
/// AB-I8-001 — <see cref="ProductionReadinessGateEvaluator"/>: fail-closed puro (nunca fabrica Pass para
/// controle ausente/incoerente), e <see cref="ProductionReadinessOutcome.ReadyForCanary"/> só é alcançável
/// quando TODOS os 32 controles do catálogo estão Pass.
/// </summary>
public sealed class ProductionReadinessGateEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    [Fact]
    public void AnEmptyResolvedDictionaryProducesNotReadyWithEveryControlAsABlocker()
    {
        var evaluation = ProductionReadinessGateEvaluator.Evaluate(
            new Dictionary<ReadinessControlId, ReadinessControlResult>(), Now);

        Assert.Equal(ProductionReadinessOutcome.NotReady, evaluation.Outcome);
        Assert.Equal(ReadinessControlCatalog.AllControls.Count, evaluation.Blockers.Count);
        Assert.Equal(ReadinessControlCatalog.AllControls.Count, evaluation.ControlResults.Count);
        Assert.All(evaluation.ControlResults, result => Assert.Equal(ReadinessControlStatus.NotMeasured, result.Status));
        Assert.All(evaluation.Blockers, blocker => Assert.Equal("CONTROL_EVIDENCE_MISSING", blocker.ReasonCode));
    }

    [Fact]
    public void WhenEveryCatalogControlIsResolvedAsPassTheOutcomeIsReadyForCanary()
    {
        var resolved = AllControlsPassing();

        var evaluation = ProductionReadinessGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(ProductionReadinessOutcome.ReadyForCanary, evaluation.Outcome);
        Assert.Empty(evaluation.Blockers);
        Assert.Equal(ReadinessControlCatalog.AllControls.Count, evaluation.ControlResults.Count);
        Assert.All(evaluation.ControlResults, result => Assert.Equal(ReadinessControlStatus.Pass, result.Status));
    }

    [Fact]
    public void ASingleNonPassControlKeepsTheOutcomeNotReadyEvenWhenAllOthersPass()
    {
        var resolved = AllControlsPassing();
        var blockedControlId = new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");
        resolved[blockedControlId] = ReadinessControlResult.Create(
            blockedControlId, ReadinessGateGroup.Security, ReadinessControlStatus.NotPerformed,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "pentest-readiness:v1"), "PENTEST_NOT_PERFORMED", Now);

        var evaluation = ProductionReadinessGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(ProductionReadinessOutcome.NotReady, evaluation.Outcome);
        var blocker = Assert.Single(evaluation.Blockers);
        Assert.Equal(blockedControlId, blocker.ControlId);
        Assert.Equal(ReadinessControlStatus.NotPerformed, blocker.Status);
    }

    [Fact]
    public void AControlResultUnderTheWrongGroupIsTreatedAsMissingRatherThanTrusted()
    {
        var resolved = AllControlsPassing();
        var controlId = new ReadinessControlId("SEC.PENTEST_NO_OPEN_CRITICAL_HIGH");

        // Constrói um resultado Pass para o controle certo, mas sob um Group incoerente com o catálogo —
        // o avaliador nunca confia cegamente nisso.
        resolved[controlId] = ReadinessControlResult.Create(
            controlId, ReadinessGateGroup.Architecture, ReadinessControlStatus.Pass,
            ReadinessEvidenceReference.SystemDerived(SomeFingerprint, "tampered"), reasonCode: string.Empty, Now);

        var evaluation = ProductionReadinessGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(ProductionReadinessOutcome.NotReady, evaluation.Outcome);
        Assert.Contains(evaluation.Blockers, blocker => blocker.ControlId == controlId && blocker.ReasonCode == "CONTROL_RESULT_MISMATCH");
    }

    [Fact]
    public void ControlResultsArePreservedInTheDeterministicCatalogOrder()
    {
        var evaluation = ProductionReadinessGateEvaluator.Evaluate(
            new Dictionary<ReadinessControlId, ReadinessControlResult>(), Now);

        var expectedOrder = ReadinessControlCatalog.AllControls.Select(definition => definition.Id).ToList();
        var actualOrder = evaluation.ControlResults.Select(result => result.ControlId).ToList();
        Assert.Equal(expectedOrder, actualOrder);
    }

    [Fact]
    public void EvaluatingTheSameInputTwiceIsDeterministic()
    {
        var resolved = AllControlsPassing();

        var first = ProductionReadinessGateEvaluator.Evaluate(resolved, Now);
        var second = ProductionReadinessGateEvaluator.Evaluate(resolved, Now);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Blockers.Count, second.Blockers.Count);
        Assert.Equal(
            first.ControlResults.Select(r => (r.ControlId, r.Status)),
            second.ControlResults.Select(r => (r.ControlId, r.Status)));
    }

    private static Dictionary<ReadinessControlId, ReadinessControlResult> AllControlsPassing()
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, Now);
        }

        return resolved;
    }
}
