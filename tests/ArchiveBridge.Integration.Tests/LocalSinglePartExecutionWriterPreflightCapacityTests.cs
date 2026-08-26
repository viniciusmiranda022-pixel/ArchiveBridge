using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-004 blocker 2 — prova, de forma DETERMINÍSTICA (via o seam interno <c>IScratchSpaceProbe</c>, sem
/// depender do espaço livre REAL do disco onde os testes rodam), que o gate de capacidade de
/// <see cref="LocalSinglePartExecutionWriter"/> agora é decidido pela fórmula do runbook
/// (<see cref="ScratchCapacityFormula"/>/<see cref="ScratchCapacityAssessor"/>): espaço exatamente
/// suficiente materializa o output normalmente, 1 byte abaixo do requisito calculado falha fechado sem
/// nenhum efeito no disco, capacidade indeterminável (sonda devolve <see langword="null"/>) NUNCA vira
/// "pode prosseguir", e overflow aritmético na combinação com a margem legada configurada também falha
/// fechado — em todos os casos de falha, nenhum diretório de staging OU final é criado (a checagem ocorre
/// antes de qualquer I/O de escrita).
/// </summary>
public sealed class LocalSinglePartExecutionWriterPreflightCapacityTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "ab_exec_preflight_src_" + Guid.NewGuid().ToString("N"));
    private readonly string _outputRoot = Path.Combine(Path.GetTempPath(), "ab_exec_preflight_out_" + Guid.NewGuid().ToString("N"));

    public LocalSinglePartExecutionWriterPreflightCapacityTests()
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

    /// <summary>Requisito calculado pela mesma fórmula/mapeamento usado pelo writer, para os testes provarem exatamente o limite.</summary>
    private static long RequiredScratchBytesFor(int sourceSizeBytes, long minFreeSpaceMarginBytes)
    {
        Assert.True(ScratchCapacityFormula.TryCompute(
            new ScratchCapacityInputs(SourceCopyBytes: 0, ExpectedPartBytes: sourceSizeBytes, RepairBackupBytes: 0, EngineTemporaryOverheadBytes: 0),
            out var formulaRequired, out _));
        var legacyRequired = sourceSizeBytes + minFreeSpaceMarginBytes;
        return Math.Max(formulaRequired, legacyRequired);
    }

    [Fact]
    public async Task ExactlySufficientAvailableSpaceMaterializesTheOutputNormally()
    {
        const string relative = "exact.pst";
        var bytes = ValidHeaderBytes(4096);
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);
        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);

        // MinFreeSpaceMarginBytes = 0 para que o requisito seja governado pela fórmula (20% de margem sobre
        // a origem) — a sonda devolve EXATAMENTE esse requisito, o limite mínimo do caminho feliz.
        var required = RequiredScratchBytesFor(bytes.Length, minFreeSpaceMarginBytes: 0);
        var writer = BuildWriter(minFreeSpaceMarginBytes: 0, new FixedScratchSpaceProbe(required));

        var artifact = await writer.ExecuteAsync(
            scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(bytes.Length, artifact.OutputSizeBytes);
        var finalDir = FinalDir(scope, plan, part);
        Assert.True(Directory.Exists(finalDir));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(finalDir, "part.pst")));
    }

    [Fact]
    public async Task OneByteBelowTheRequiredScratchFailsClosedWithoutAnyDiskEffect()
    {
        const string relative = "one-below.pst";
        var bytes = ValidHeaderBytes(4096);
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);
        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);

        var required = RequiredScratchBytesFor(bytes.Length, minFreeSpaceMarginBytes: 0);
        var writer = BuildWriter(minFreeSpaceMarginBytes: 0, new FixedScratchSpaceProbe(required - 1));

        var exception = await Assert.ThrowsAsync<PartitionExecutionLimitExceededException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_SPACE", exception.ReasonCode);
        AssertNoDiskEffect(scope, plan, part);
    }

    [Fact]
    public async Task UnknownAvailableSpaceNeverBecomesEnoughAndFailsClosed()
    {
        const string relative = "unknown.pst";
        var bytes = ValidHeaderBytes(4096);
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);
        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);

        // A sonda devolve null (indeterminável) mesmo que o requisito calculado seja minúsculo — Unknown
        // nunca é tratado como "sem limite"/"pode prosseguir" (AB-I7-003/004 §4).
        var writer = BuildWriter(minFreeSpaceMarginBytes: 0, new FixedScratchSpaceProbe(availableBytes: null));

        var exception = await Assert.ThrowsAsync<PartitionExecutionLimitExceededException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_SPACE", exception.ReasonCode);
        AssertNoDiskEffect(scope, plan, part);
    }

    [Fact]
    public async Task OverflowingArithmeticBetweenTheFormulaAndTheLegacyMarginFailsClosed()
    {
        const string relative = "overflow.pst";
        var bytes = ValidHeaderBytes(4096);
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);
        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);

        // expectedSize + MinFreeSpaceMarginBytes transborda long — a checagem `checked` do writer captura o
        // overflow e falha fechado, mesmo que a sonda reporte espaço disponível "infinito" (long.MaxValue).
        var writer = BuildWriter(minFreeSpaceMarginBytes: long.MaxValue - 10, new FixedScratchSpaceProbe(long.MaxValue));

        var exception = await Assert.ThrowsAsync<PartitionExecutionLimitExceededException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_SPACE", exception.ReasonCode);
        AssertNoDiskEffect(scope, plan, part);
    }

    private LocalSinglePartExecutionWriter BuildWriter(long minFreeSpaceMarginBytes, IScratchSpaceProbe probe) =>
        new(
            new PstStorageOptions { RootPath = _sourceRoot },
            new PartitionExecutionOutputOptions
            {
                RootPath = _outputRoot,
                MinFreeSpaceMarginBytes = minFreeSpaceMarginBytes,
                Timeout = TimeSpan.FromSeconds(30),
            },
            new SystemClock(),
            PhysicalPstArtifactStreamFactory.Instance,
            probe);

    private string FinalDir(TenantScope scope, PartitionPlan plan, PartitionPlanPart part) =>
        Path.Combine(
            _outputRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"), part.PartKey.Value);

    private void AssertNoDiskEffect(TenantScope scope, PartitionPlan plan, PartitionPlanPart part)
    {
        Assert.False(Directory.Exists(FinalDir(scope, plan, part)));

        var stagingRoot = Path.Combine(_outputRoot, ".staging");
        if (Directory.Exists(stagingRoot))
        {
            // O gate lança ANTES de qualquer diretório de staging ser criado — nunca há nem um órfão aqui.
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
        }
    }

    private static byte[] ValidHeaderBytes(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0x21; bytes[1] = 0x42; bytes[2] = 0x44; bytes[3] = 0x4E;
        bytes[8] = 0x53; bytes[9] = 0x4D;
        bytes[10] = 36; bytes[11] = 0;
        return bytes;
    }

    private static (TenantScope Scope, MigrationArtifact Custody, PartitionPlan Plan, PartitionPlanPart Part) BuildPlanFor(
        string relative, byte[] bytes)
    {
        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var hash = DeterministicHash.ComputeBytes(bytes);
        var custody = MigrationArtifact.Register(
            scope.Tenant, scope.Project, new PstRelativePath(relative), hash, bytes.Length, DateTimeOffset.UtcNow);
        var planner = new PartitionPlannerIdentity("TestPlanner", "1.0");
        var policy = PartitionPolicy.Create(bytes.Length * 2L, bytes.Length * 4L);
        var source = new PartitionPlanSource(custody.Id, InspectionId.New(), hash, bytes.Length, "Engine", "1.0");
        var planHash = PartitionPlanIdentity.ComputePlanHash(scope.Tenant, scope.Project, source, policy, planner);
        var part = new PartitionPlanPart(
            PartitionPlanPartId.New(), 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), bytes.Length, coversEntireSource: true);
        var plan = PartitionPlan.Create(
            PartitionPlanId.New(), scope.Tenant, scope.Project, source, policy, planner, planHash,
            PartitionPlanReason.SinglePartWithinTarget, [part], CorrelationId.New(), DateTimeOffset.UtcNow);
        return (scope, custody, plan, part);
    }

    /// <summary>Sonda determinística — devolve sempre o mesmo valor fixo (ou <see langword="null"/> para "indeterminável").</summary>
    private sealed class FixedScratchSpaceProbe(long? availableBytes) : IScratchSpaceProbe
    {
        public long? AvailableFreeSpaceBytes(string rootPath) => availableBytes;
    }
}
