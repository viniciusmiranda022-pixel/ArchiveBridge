using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using Xunit;

namespace ArchiveBridge.Domain.Tests.Canary;

/// <summary>
/// AB-I8-004 — <see cref="CanaryScenarioResult"/>: Pass estruturalmente impossível sem evidência real, e
/// convergência/tamper-evidence determinística de <see cref="CanaryScenarioResult.ComputeContentFingerprint"/>/
/// <see cref="CanaryScenarioResult.ComputeRecordHash"/>.
/// </summary>
public sealed class CanaryScenarioResultTests
{
    private static readonly CanaryScenarioId ScenarioId = new("CANARY.CRASH_RECOVERY");
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly Sha256Hash OtherHash = new(new string('b', 64));
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateThrowsWhenStatusIsPassButEvidenceIsNone()
    {
        Assert.Throws<ArgumentException>(() => CanaryScenarioResult.Create(
            ScenarioId, CanaryScenarioStatus.Pass, CanaryEvidenceReference.None, reasonCode: string.Empty, Now));
    }

    [Fact]
    public void CreateSucceedsWhenStatusIsPassWithRealEvidence()
    {
        var result = CanaryScenarioResult.Create(
            ScenarioId, CanaryScenarioStatus.Pass, CanaryEvidenceReference.SystemDerived(SomeHash, "locator"),
            reasonCode: string.Empty, Now);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
    }

    [Fact]
    public void PendingProducesAFailClosedResultWithNoEvidence()
    {
        var result = CanaryScenarioResult.Pending(ScenarioId, "SCENARIO_EVIDENCE_MISSING", Now);

        Assert.Equal(CanaryScenarioStatus.Pending, result.Status);
        Assert.Equal(CanaryEvidenceKind.None, result.Evidence.Kind);
    }

    [Fact]
    public void ContentFingerprintIsDeterministicForIdenticalInputs()
    {
        var evidence = CanaryEvidenceReference.OperatorAttested(SomeHash, "locator");

        var first = CanaryScenarioResult.ComputeContentFingerprint(ScenarioId, CanaryScenarioStatus.Pass, evidence, "reason", Now);
        var second = CanaryScenarioResult.ComputeContentFingerprint(ScenarioId, CanaryScenarioStatus.Pass, evidence, "reason", Now);

        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void ContentFingerprintChangesWhenStatusChanges()
    {
        var evidence = CanaryEvidenceReference.OperatorAttested(SomeHash, "locator");

        var pass = CanaryScenarioResult.ComputeContentFingerprint(ScenarioId, CanaryScenarioStatus.Pass, evidence, "reason", Now);
        var fail = CanaryScenarioResult.ComputeContentFingerprint(ScenarioId, CanaryScenarioStatus.Fail, evidence, "reason", Now);

        Assert.NotEqual(pass.Value, fail.Value);
    }

    [Fact]
    public void ContentFingerprintChangesWhenEvidenceFingerprintChanges()
    {
        var first = CanaryScenarioResult.ComputeContentFingerprint(
            ScenarioId, CanaryScenarioStatus.Pass, CanaryEvidenceReference.OperatorAttested(SomeHash, "locator"), "reason", Now);
        var second = CanaryScenarioResult.ComputeContentFingerprint(
            ScenarioId, CanaryScenarioStatus.Pass, CanaryEvidenceReference.OperatorAttested(OtherHash, "locator"), "reason", Now);

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void RecordHashIsSensitiveToEveryPersistedField()
    {
        var tenant = Guid.NewGuid();
        var project = Guid.NewGuid();
        var evidence = CanaryEvidenceReference.OperatorAttested(SomeHash, "locator");
        var contentFingerprint = CanaryScenarioResult.ComputeContentFingerprint(ScenarioId, CanaryScenarioStatus.Pass, evidence, "reason", Now);
        var correlation = CorrelationId.New();

        var baseline = CanaryScenarioResult.ComputeRecordHash(
            tenant, project, planVersion: 1, ScenarioId, resultVersion: 1, CanaryScenarioStatus.Pass, evidence, "reason", Now,
            "actor", "Operator", correlation, Now, "schema.v1", contentFingerprint);

        var withDifferentActor = CanaryScenarioResult.ComputeRecordHash(
            tenant, project, planVersion: 1, ScenarioId, resultVersion: 1, CanaryScenarioStatus.Pass, evidence, "reason", Now,
            "different-actor", "Operator", correlation, Now, "schema.v1", contentFingerprint);

        var withDifferentResultVersion = CanaryScenarioResult.ComputeRecordHash(
            tenant, project, planVersion: 1, ScenarioId, resultVersion: 2, CanaryScenarioStatus.Pass, evidence, "reason", Now,
            "actor", "Operator", correlation, Now, "schema.v1", contentFingerprint);

        Assert.NotEqual(baseline.Value, withDifferentActor.Value);
        Assert.NotEqual(baseline.Value, withDifferentResultVersion.Value);
    }
}
