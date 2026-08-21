using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-4B-006 — prova, de forma DETERMINÍSTICA (sem depender de timing de rede/disco real), que
/// <see cref="LocalSinglePartExecutionWriter"/> nunca publica um output canônico quando a cópia da origem
/// excede o timeout configurado OU quando o próprio chamador cancela — nos dois casos, nenhum diretório
/// final é criado e nenhum staging sobrevive. Não usa SQL Server: mesmo padrão de
/// <c>HeaderOnlyPstInspectionEngineLimitEnforcementTests</c> (seam interno
/// <see cref="IPstArtifactStreamFactory"/> injetando um stream que nunca termina de ler).
/// </summary>
public sealed class LocalSinglePartExecutionWriterLimitEnforcementTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "ab_exec_limit_src_" + Guid.NewGuid().ToString("N"));
    private readonly string _outputRoot = Path.Combine(Path.GetTempPath(), "ab_exec_limit_out_" + Guid.NewGuid().ToString("N"));

    public LocalSinglePartExecutionWriterLimitEnforcementTests()
    {
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_outputRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ACopyThatNeverCompletesIsAbortedByTheConfiguredTimeoutWithoutPublishingAnyOutput()
    {
        const string relative = "hangs.pst";
        var bytes = new byte[] { 0x21, 0x42, 0x44, 0x4E, 0, 0, 0, 0, 0x53, 0x4D, 36, 0 };
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);

        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);
        var sourceOptions = new PstStorageOptions { RootPath = _sourceRoot };
        var outputOptions = new PartitionExecutionOutputOptions
        {
            RootPath = _outputRoot,
            MinFreeSpaceMarginBytes = 0,
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var writer = new LocalSinglePartExecutionWriter(
            sourceOptions, outputOptions, new SystemClock(), new FixedStreamFactory(new NeverCompletingStream()));

        var exception = await Assert.ThrowsAsync<PartitionExecutionLimitExceededException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Equal("TIMEOUT", exception.ReasonCode);
        AssertNoCanonicalOutputAndNoOrphanStaging(scope, plan, part);
    }

    [Fact]
    public async Task CallerCancellationDuringTheCopyNeverPublishesAnyOutput()
    {
        const string relative = "cancelled.pst";
        var bytes = new byte[] { 0x21, 0x42, 0x44, 0x4E, 0, 0, 0, 0, 0x53, 0x4D, 36, 0 };
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);

        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);
        var sourceOptions = new PstStorageOptions { RootPath = _sourceRoot };
        var outputOptions = new PartitionExecutionOutputOptions
        {
            RootPath = _outputRoot,
            MinFreeSpaceMarginBytes = 0,
            Timeout = TimeSpan.FromMinutes(10), // bem maior que o cancelamento do chamador, abaixo.
        };
        var writer = new LocalSinglePartExecutionWriter(
            sourceOptions, outputOptions, new SystemClock(), new FixedStreamFactory(new NeverCompletingStream()));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow), cts.Token));

        AssertNoCanonicalOutputAndNoOrphanStaging(scope, plan, part);
    }

    private void AssertNoCanonicalOutputAndNoOrphanStaging(TenantScope scope, PartitionPlan plan, PartitionPlanPart part)
    {
        var finalDir = Path.Combine(
            _outputRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"), part.PartKey.Value);
        Assert.False(Directory.Exists(finalDir));

        var stagingRoot = Path.Combine(_outputRoot, ".staging");
        if (Directory.Exists(stagingRoot))
        {
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot)); // limpo (melhor esforço) na falha controlada.
        }
    }

    private static (TenantScope Scope, MigrationArtifact Custody, PartitionPlan Plan, PartitionPlanPart Part) BuildPlanFor(
        string relative, byte[] bytes)
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var hash = DeterministicHash.ComputeBytes(bytes);
        var custody = MigrationArtifact.Register(
            scope.Tenant, scope.Project, new PstRelativePath(relative), hash, bytes.Length, DateTimeOffset.UtcNow);
        var planner = new PartitionPlannerIdentity("TestPlanner", "1.0");
        var policy = PartitionPolicy.Create(bytes.Length * 2, bytes.Length * 4);
        var source = new PartitionPlanSource(custody.Id, InspectionId.New(), hash, bytes.Length, "Engine", "1.0");
        var planHash = PartitionPlanIdentity.ComputePlanHash(scope.Tenant, scope.Project, source, policy, planner);
        var part = new PartitionPlanPart(
            PartitionPlanPartId.New(), 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), bytes.Length, coversEntireSource: true);
        var plan = PartitionPlan.Create(
            PartitionPlanId.New(), scope.Tenant, scope.Project, source, policy, planner, planHash,
            PartitionPlanReason.SinglePartWithinTarget, [part], CorrelationId.New(), DateTimeOffset.UtcNow);
        return (scope, custody, plan, part);
    }

    private sealed class FixedStreamFactory(Stream stream) : IPstArtifactStreamFactory
    {
        public Stream OpenRead(string absolutePath) => stream;
    }

    /// <summary>Stream sintético cujo <c>ReadAsync</c> nunca completa por conta própria — só retorna/lança quando o token informado é cancelado. Simula um I/O pendurado (disco lento/travado, rede) de forma determinística.</summary>
    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0; // inalcançável: Task.Delay(Infinite, ct) só retorna lançando quando ct é cancelado.
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
