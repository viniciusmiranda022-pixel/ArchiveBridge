using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>Fábricas e builders compartilhados pelos testes de integração da Slice 4B (PST Inspection).</summary>
internal static class Slice4bPstProcessingSupport
{
    private static readonly IClock DefaultClock = new SystemClock();

    /// <summary>Raiz local isolada dos artefatos PST desta execução de testes (subpasta do artifact root da fixture).</summary>
    public static string PstRoot(SqlServerFixture fixture) => Path.Combine(fixture.ArtifactRoot, "pst");

    public static SqlPstCustodyStore CustodyStore(SqlServerFixture fixture, IClock? clock = null) =>
        new(fixture.Factory, clock ?? DefaultClock);

    public static SqlPstInspectionStore InspectionStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static HeaderOnlyPstInspectionEngine Engine(SqlServerFixture fixture, PstStorageOptions? options = null) =>
        new(CustodyStore(fixture), options ?? DefaultOptions(fixture));

    public static InspectPstArtifactUseCase UseCase(SqlServerFixture fixture, IClock? clock = null, PstStorageOptions? options = null)
    {
        var effectiveClock = clock ?? DefaultClock;
        return new InspectPstArtifactUseCase(
            CustodyStore(fixture, effectiveClock), InspectionStore(fixture), Engine(fixture, options), effectiveClock);
    }

    public static PstStorageOptions DefaultOptions(SqlServerFixture fixture) => new() { RootPath = PstRoot(fixture) };

    // ---- Slice 4B, Passo 2 (Partition Planning) ----

    public static SqlPartitionPlanStore PlanStore(SqlServerFixture fixture) => new(fixture.Factory);

    public static SizeBoundedPartitionPlanner Planner(PartitionPolicy? policy = null) =>
        new(policy ?? PartitionPolicy.RunbookDefault);

    /// <summary>Caso de uso de PLANEJAMENTO — note que ele não recebe a engine: planejar é read-only.</summary>
    public static PlanPstPartitionUseCase PlanUseCase(
        SqlServerFixture fixture, PartitionPolicy? policy = null, IClock? clock = null)
    {
        var effectiveClock = clock ?? DefaultClock;
        return new PlanPstPartitionUseCase(
            CustodyStore(fixture, effectiveClock), InspectionStore(fixture), PlanStore(fixture),
            Planner(policy), effectiveClock);
    }

    /// <summary>Escreve bytes de teste sob a raiz de custódia PST e devolve o caminho relativo gravado.</summary>
    public static string WriteFile(SqlServerFixture fixture, string relativeName, byte[] content)
    {
        var root = PstRoot(fixture);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, relativeName), content);
        return relativeName;
    }

    /// <summary>Cabeçalho PST Unicode 2013+ (wVer=36) sintético válido, preenchido de forma determinística até <paramref name="totalSize"/>.</summary>
    public static byte[] ValidUnicodeHeader(int totalSize = 4096)
    {
        var bytes = new byte[totalSize];
        bytes[0] = 0x21; bytes[1] = 0x42; bytes[2] = 0x44; bytes[3] = 0x4E; // dwMagic "!BDN"
        bytes[8] = 0x53; bytes[9] = 0x4D;                                  // wMagicClient "SM"
        bytes[10] = 36; bytes[11] = 0;                                     // wVer = 36 (little-endian)
        for (var i = 12; i < totalSize; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    public static byte[] InvalidSignatureBytes(int totalSize = 64)
    {
        var bytes = new byte[totalSize];
        for (var i = 0; i < totalSize; i++)
        {
            bytes[i] = (byte)(i % 200 + 1);
        }

        return bytes;
    }

    public static byte[] UnsupportedVersionHeader(int totalSize = 64)
    {
        var bytes = ValidUnicodeHeader(totalSize);
        bytes[10] = 99; bytes[11] = 0; // wVer desconhecido
        return bytes;
    }
}
