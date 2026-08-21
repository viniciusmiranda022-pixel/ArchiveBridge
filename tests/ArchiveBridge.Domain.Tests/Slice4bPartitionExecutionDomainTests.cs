using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// Slice 4B, Passo 3 — regras de EXECUÇÃO de particionamento no Domain: o invariante central de
/// <see cref="PartitionPlanReason.SinglePartWithinTarget"/> é que o output tem de ser cópia byte-for-byte
/// exata da origem, identidade de parte consistente com o plano, e a mesma fronteira Create/Rehydrate NÃO
/// CONFIÁVEL já aplicada a <see cref="PartitionPlan"/>.
/// </summary>
public sealed class Slice4bPartitionExecutionDomainTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddSeconds(5);
    private static readonly PartitionExecutorIdentity Executor = new("TestExecutor", "1.0");

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static (TenantId Tenant, ProjectId Project, ArtifactId Artifact, PartitionPlanId Plan, PartitionPlanPartId Part) NewScope() =>
        (new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), ArtifactId.New(), PartitionPlanId.New(), PartitionPlanPartId.New());

    private static PartitionExecutionRecord Complete(
        (TenantId Tenant, ProjectId Project, ArtifactId Artifact, PartitionPlanId Plan, PartitionPlanPartId Part) scope,
        Sha256Hash planHash,
        Sha256Hash sourceHash,
        long sourceSize,
        Sha256Hash outputHash,
        long outputSize,
        int sequence = 1) =>
        PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, sequence, PartitionPlanIdentity.ComputePartKey(planHash, sequence), sourceHash, sourceSize,
            outputHash, outputSize, Executor, CorrelationId.New(), StartedAt, CompletedAt);

    // ---- Invariante central: output byte-for-byte idêntico à origem ----

    [Fact]
    public void CompleteAcceptsAnOutputThatIsByteForByteIdenticalToTheSource()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source-bytes");

        var record = Complete(scope, planHash, sourceHash, 4096, sourceHash, 4096);

        Assert.Equal(sourceHash, record.OutputHash);
        Assert.Equal(4096, record.OutputSizeBytes);
        Assert.True(record.HasConsistentIdentity());
    }

    [Fact]
    public void CompleteRejectsAnOutputHashThatDiffersFromTheSource()
    {
        var scope = NewScope();
        var planHash = Hash("plan");

        Assert.Throws<ArgumentException>(() =>
            Complete(scope, planHash, Hash("source"), 4096, Hash("different-output"), 4096));
    }

    [Fact]
    public void CompleteRejectsAnOutputSizeThatDiffersFromTheSource()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");

        Assert.Throws<ArgumentException>(() =>
            Complete(scope, planHash, sourceHash, 4096, sourceHash, 4095));
    }

    // ---- Identidade determinística da parte (liga a execução ao plano exato) ----

    [Fact]
    public void PartKeyMustBeTheDeterministicKeyDerivedFromPlanHashAndSequence()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");
        var wrongPartKey = PartitionPlanIdentity.ComputePartKey(Hash("other-plan"), 1);

        Assert.Throws<ArgumentException>(() => PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, wrongPartKey, sourceHash, 4096, sourceHash, 4096, Executor, CorrelationId.New(),
            StartedAt, CompletedAt));
    }

    [Fact]
    public void SequenceBelowOneIsRejected()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");

        Assert.Throws<ArgumentException>(() => PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 0, PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096,
            Executor, CorrelationId.New(), StartedAt, CompletedAt));
    }

    // ---- Escopo/tempo ----

    [Fact]
    public void EmptyTenantIsRejected()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");

        Assert.Throws<ArgumentException>(() => PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), default, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096,
            Executor, CorrelationId.New(), StartedAt, CompletedAt));
    }

    [Fact]
    public void EmptyProjectIsRejected()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");

        Assert.Throws<ArgumentException>(() => PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, default, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096,
            Executor, CorrelationId.New(), StartedAt, CompletedAt));
    }

    [Fact]
    public void CompletedBeforeStartedIsRejected()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");

        Assert.Throws<ArgumentException>(() => PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096,
            Executor, CorrelationId.New(), StartedAt, StartedAt.AddSeconds(-1)));
    }

    [Fact]
    public void NegativeSizesAreRejected()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");
        var partKey = PartitionPlanIdentity.ComputePartKey(planHash, 1);

        Assert.Throws<ArgumentException>(() => PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, partKey, sourceHash, -1, sourceHash, -1, Executor, CorrelationId.New(),
            StartedAt, CompletedAt));
    }

    // ---- Fronteira Create/Rehydrate: a persistência é NÃO CONFIÁVEL ----

    [Fact]
    public void RehydrateAppliesTheSameInvariantsButThrowsTheIntegrityException()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");
        var partKey = PartitionPlanIdentity.ComputePartKey(planHash, 1);

        Assert.Throws<PartitionExecutionIntegrityViolationException>(() => PartitionExecutionRecord.Rehydrate(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, partKey, sourceHash, 4096, Hash("tampered-output"), 4096, Executor, CorrelationId.New(),
            StartedAt, CompletedAt));
    }

    [Fact]
    public void RehydrateAcceptsAConsistentlyPersistedRow()
    {
        var scope = NewScope();
        var planHash = Hash("plan");
        var sourceHash = Hash("source");
        var partKey = PartitionPlanIdentity.ComputePartKey(planHash, 1);

        var record = PartitionExecutionRecord.Rehydrate(
            PartitionExecutionId.New(), scope.Tenant, scope.Project, scope.Artifact, scope.Plan, scope.Part,
            planHash, 1, partKey, sourceHash, 4096, sourceHash, 4096, Executor, CorrelationId.New(),
            StartedAt, CompletedAt);

        Assert.True(record.HasConsistentIdentity());
    }

    [Fact]
    public void ExecutorIdentityRejectsEmptyName() =>
        Assert.Throws<ArgumentException>(() => new PartitionExecutorIdentity("", "1.0"));

    [Fact]
    public void ExecutorIdentityRejectsEmptyVersion() =>
        Assert.Throws<ArgumentException>(() => new PartitionExecutorIdentity("Name", ""));
}
