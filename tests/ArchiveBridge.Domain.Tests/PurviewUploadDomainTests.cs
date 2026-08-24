using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using Xunit;

namespace ArchiveBridge.Domain.Tests;

/// <summary>
/// AB-I5-009 — regras de Domain do upload Purview: catálogo de homologação AzCopy (versão E hash exatos),
/// estrutura remota opaca/exclusiva, identidade do pedido lógico com fronteira Create/Rehydrate NÃO
/// CONFIÁVEL, e determinismo da identidade lógica do upload (item 14).
/// </summary>
public sealed class PurviewUploadDomainTests
{
    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    // ---- AzCopyHomologationCatalog (item 5) ----

    [Fact]
    public void IsHomologatedRequiresBothVersionAndHashToMatchExactly()
    {
        var catalog = new AzCopyHomologationCatalog([new AzCopyBinaryIdentity("10.25.0", Hash("binary-v1"))]);

        Assert.True(catalog.IsHomologated(new AzCopyBinaryIdentity("10.25.0", Hash("binary-v1"))));
        Assert.False(catalog.IsHomologated(new AzCopyBinaryIdentity("10.25.0", Hash("tampered-binary"))));
        Assert.False(catalog.IsHomologated(new AzCopyBinaryIdentity("10.26.0", Hash("binary-v1"))));
    }

    [Fact]
    public void CatalogConstructionRejectsAnEmptyHomologatedSet()
    {
        Assert.Throws<ArgumentException>(() => new AzCopyHomologationCatalog([]));
    }

    // ---- Estrutura remota (item 4 / acceptance criteria 11) ----

    [Fact]
    public void RemoteUploadPrefixIsExclusiveAndDifferentAcrossTenantProjectOrWave()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();

        var prefix = PurviewRemoteUploadPrefix.ForWave(tenant, project, wave);
        Assert.StartsWith("ingestiondata/", prefix.Value, StringComparison.Ordinal);
        Assert.Equal(prefix.Value, $"ingestiondata/{prefix.WaveSegment}");

        var differentWave = PurviewRemoteUploadPrefix.ForWave(tenant, project, WaveId.New());
        Assert.NotEqual(prefix.Value, differentWave.Value);

        var differentProject = PurviewRemoteUploadPrefix.ForWave(tenant, new ProjectId(Guid.NewGuid()), wave);
        Assert.NotEqual(prefix.Value, differentProject.Value);
    }

    [Fact]
    public void RemoteUploadPrefixIsStructurallyOpaqueHexWithoutTraversalOrSeparators()
    {
        var prefix = PurviewRemoteUploadPrefix.ForWave(
            new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), WaveId.New());

        Assert.DoesNotContain("..", prefix.WaveSegment, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', prefix.WaveSegment);
        foreach (var character in prefix.WaveSegment)
        {
            Assert.True((character is >= '0' and <= '9') || (character is >= 'a' and <= 'f') || character == '-');
        }
    }

    [Fact]
    public void RemotePstNameIsDerivedFromArtifactAndSequenceNeverFromMailboxOrPath()
    {
        var artifact = ArtifactId.New();
        var name = PurviewRemotePstName.ForPart(artifact, 3);

        Assert.Equal($"p_{artifact.Value:N}_part003.pst", name.Value);
        Assert.EndsWith(".pst", name.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RemotePstNameRejectsANonPositiveSequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PurviewRemotePstName.ForPart(ArtifactId.New(), 0));
    }

    // ---- PurviewUploadRequest — fronteira Create/Rehydrate NÃO CONFIÁVEL ----

    [Fact]
    public void RehydrateFailsClosedWhenRequestHashDoesNotMatchLoadedFields()
    {
        var request = PurviewUploadRequest.Create(
            PurviewUploadRequestId.New(), new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), WaveId.New(),
            JobId.New(), CorrelationId.New(), DateTimeOffset.UtcNow);

        Assert.Throws<PurviewUploadIntegrityViolationException>(() =>
            PurviewUploadRequest.Rehydrate(
                request.Id, request.Tenant, request.Project, request.Wave, request.Job, request.Correlation,
                request.CreatedAtUtc, Hash("tampered")));
    }

    [Fact]
    public void RehydrateSucceedsWhenTheHashMatchesTheLoadedFields()
    {
        var request = PurviewUploadRequest.Create(
            PurviewUploadRequestId.New(), new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), WaveId.New(),
            JobId.New(), CorrelationId.New(), DateTimeOffset.UtcNow);

        var rehydrated = PurviewUploadRequest.Rehydrate(
            request.Id, request.Tenant, request.Project, request.Wave, request.Job, request.Correlation,
            request.CreatedAtUtc, request.RequestHash);

        Assert.Equal(request, rehydrated);
    }

    [Fact]
    public void CreateRejectsAnEmptyTenantOrProject()
    {
        Assert.Throws<ArgumentException>(() => PurviewUploadRequest.Create(
            PurviewUploadRequestId.New(), default, new ProjectId(Guid.NewGuid()), WaveId.New(), JobId.New(),
            CorrelationId.New(), DateTimeOffset.UtcNow));
    }

    // ---- PurviewUploadRequestIdentity — determinismo (item 14) ----

    private static readonly DateTimeOffset StartedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PartitionExecutorIdentity Executor = new("TestExecutor", "1.0");

    private static WavePartitionOutputBinding NewBinding(TenantId tenant, ProjectId project, WaveId wave)
    {
        var planHash = Hash("plan");
        var sourceHash = Hash("source-bytes");
        var execution = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), tenant, project, ArtifactId.New(), PartitionPlanId.New(), PartitionPlanPartId.New(),
            planHash, 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096, Executor,
            CorrelationId.New(), StartedAt, StartedAt.AddSeconds(5));
        return WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), tenant, project, wave, execution, CorrelationId.New(), StartedAt);
    }

    [Fact]
    public void ComputeIsDeterministicRegardlessOfBindingReadOrder()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var bindingA = NewBinding(tenant, project, wave);
        var bindingB = NewBinding(tenant, project, wave);
        var sasHandleId = Guid.NewGuid();
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("binary"));
        var prefix = PurviewRemoteUploadPrefix.ForWave(tenant, project, wave);

        var forward = PurviewUploadRequestIdentity.Compute([bindingA, bindingB], sasHandleId, 1, binary, prefix);
        var reversed = PurviewUploadRequestIdentity.Compute([bindingB, bindingA], sasHandleId, 1, binary, prefix);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void ComputeProducesADifferentIdentityWhenTheSasGenerationChanges()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var binding = NewBinding(tenant, project, wave);
        var sasHandleId = Guid.NewGuid();
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("binary"));
        var prefix = PurviewRemoteUploadPrefix.ForWave(tenant, project, wave);

        var first = PurviewUploadRequestIdentity.Compute([binding], sasHandleId, 1, binary, prefix);
        var second = PurviewUploadRequestIdentity.Compute([binding], sasHandleId, 2, binary, prefix);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeProducesADifferentIdentityWhenTheBindingSetChanges()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var project = new ProjectId(Guid.NewGuid());
        var wave = WaveId.New();
        var bindingA = NewBinding(tenant, project, wave);
        var bindingB = NewBinding(tenant, project, wave);
        var sasHandleId = Guid.NewGuid();
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("binary"));
        var prefix = PurviewRemoteUploadPrefix.ForWave(tenant, project, wave);

        var withOne = PurviewUploadRequestIdentity.Compute([bindingA], sasHandleId, 1, binary, prefix);
        var withTwo = PurviewUploadRequestIdentity.Compute([bindingA, bindingB], sasHandleId, 1, binary, prefix);

        Assert.NotEqual(withOne, withTwo);
    }

    [Fact]
    public void ComputeRejectsAnEmptyBindingSet()
    {
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("binary"));
        var prefix = PurviewRemoteUploadPrefix.ForWave(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()), WaveId.New());

        Assert.Throws<ArgumentException>(() =>
            PurviewUploadRequestIdentity.Compute([], Guid.NewGuid(), 1, binary, prefix));
    }
}
