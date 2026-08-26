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
/// AB-I7-001 item 2/3 (chaos cases 175 "arquivo origem muda no meio" e 171 "scratch fica sem espaço") —
/// prova, de forma DETERMINÍSTICA e sem depender de SQL Server (mesmo padrão de
/// <see cref="LocalSinglePartExecutionWriterLimitEnforcementTests"/>, seam interno
/// <see cref="IPstArtifactStreamFactory"/>), dois cenários de fault injection ainda não cobertos pelos
/// testes existentes de <see cref="Slice4bPartitionExecutionTests"/>:
/// <list type="number">
/// <item>a origem muda ENQUANTO está sendo copiada (não apenas antes de a cópia começar, já coberto por
/// <c>SourceThatDriftedOnDiskAfterPlanningIsRejectedBeforeAnyCanonicalOutputIsPublished</c>) — inclusive o
/// ramo específico em que a origem CRESCE além do tamanho observado no plano, interrompendo a leitura
/// imediatamente, nunca coberto antes;</item>
/// <item>a raiz de scratch está indisponível/quebrada para escrita (aqui simulado de forma portátil e
/// determinística — um ARQUIVO já ocupa o caminho onde o writer precisa criar um diretório — em vez de
/// depender de semântica de permissão de SO sensível a rodar como root/não-root em CI).</item>
/// </list>
/// Em todos os casos o writer nunca publica um output parcial/inconsistente no caminho final: ou o bundle
/// canônico completo existe e confere, ou não existe nada.
/// </summary>
public sealed class LocalSinglePartExecutionWriterMidCopyChaosTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "ab_midcopy_src_" + Guid.NewGuid().ToString("N"));
    private readonly string _outputRoot = Path.Combine(Path.GetTempPath(), "ab_midcopy_out_" + Guid.NewGuid().ToString("N"));

    public LocalSinglePartExecutionWriterMidCopyChaosTests()
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
    public async Task SourceThatGrowsWhileBeingCopiedIsAbortedImmediatelyWithoutPublishingAnyOutput()
    {
        var originalBytes = ValidHeaderBytes(size: 4096);
        const string relative = "grows-mid-copy.pst";
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), originalBytes);

        var (scope, custody, plan, part) = BuildPlanFor(relative, originalBytes);

        // Simula um escritor concorrente aumentando o arquivo de origem ENQUANTO o worker já está lendo-o:
        // a stream sintética entrega bytes A MAIS do que o plano observou, nunca menos e nunca truncando —
        // exercita o ramo de aborto imediato (nunca alcançado por nenhum teste existente, que só cobre
        // origem trocada ANTES do início da cópia).
        var grownBytes = new byte[originalBytes.Length + 4096];
        Array.Copy(originalBytes, grownBytes, originalBytes.Length);
        var writer = BuildWriter(new FixedStreamFactory(() => new MemoryStream(grownBytes)));

        var exception = await Assert.ThrowsAsync<PartitionExecutionSourceStaleException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Contains("cresceu", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoCanonicalOutputAndNoOrphanStaging(scope, plan, part);
    }

    [Fact]
    public async Task SourceThatIsRewrittenWithDifferentContentPartwayThroughTheReadIsRejectedWithoutPublishingAnyOutput()
    {
        var originalBytes = ValidHeaderBytes(size: 4096);
        const string relative = "mutates-mid-copy.pst";
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), originalBytes);

        var (scope, custody, plan, part) = BuildPlanFor(relative, originalBytes);

        // Mesmo tamanho da origem esperada pelo plano, mas o CONTEÚDO muda no meio da leitura — simula um
        // processo concorrente sobrescrevendo a cauda do arquivo enquanto este worker já leu o prefixo.
        // Nunca ultrapassa expectedSize (não aciona o ramo de "cresceu"): o hash final diverge do hash do
        // plano, detectado só depois que a cópia inteira termina — exatamente o mesmo desfecho fail-closed,
        // por um caminho de código diferente do teste "drifted on disk" (que reescreve ANTES de a cópia
        // começar, nunca DURANTE).
        var mutatedTail = (byte[])originalBytes.Clone();
        mutatedTail[^1] ^= 0xFF;
        var writer = BuildWriter(new MidReadMutatingStreamFactory(originalBytes, mutatedTail, flipAfterByte: originalBytes.Length / 2));

        var exception = await Assert.ThrowsAsync<PartitionExecutionSourceStaleException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.Contains("divergiu", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoCanonicalOutputAndNoOrphanStaging(scope, plan, part);
    }

    [Fact]
    public async Task AScratchRootThatIsUnavailableForWritingFailsClosedWithoutPublishingAnyOutput()
    {
        var bytes = ValidHeaderBytes(size: 1024);
        const string relative = "scratch-unavailable.pst";
        File.WriteAllBytes(Path.Combine(_sourceRoot, relative), bytes);

        var (scope, custody, plan, part) = BuildPlanFor(relative, bytes);

        // Simula scratch quebrado/indisponível de forma PORTÁTIL (independente de rodar como root ou não em
        // CI, ao contrário de uma checagem de permissão de SO): um ARQUIVO comum já ocupa o caminho em que o
        // writer precisa criar o diretório ".staging" — Directory.CreateDirectory falha com IOException
        // determinística em qualquer ambiente.
        File.WriteAllBytes(Path.Combine(_outputRoot, ".staging"), []);

        var writer = BuildWriter(new FixedStreamFactory(() => new MemoryStream(bytes)));

        await Assert.ThrowsAsync<IOException>(() =>
            writer.ExecuteAsync(
                scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow),
                CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(
            _outputRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"), part.PartKey.Value)));

        // Restaura o scratch e prova que a execução converge normalmente depois — a falha de infraestrutura
        // nunca deixa um estado permanentemente irrecuperável.
        File.Delete(Path.Combine(_outputRoot, ".staging"));
        var recovered = await BuildWriter(new FixedStreamFactory(() => new MemoryStream(bytes)))
            .ExecuteAsync(scope, custody, plan, part, new PartitionExecutionContext(CorrelationId.New(), DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(custody.RegisteredHash, recovered.SourceHash);
        Assert.True(Directory.Exists(Path.Combine(
            _outputRoot, scope.Tenant.Value.ToString("N"), scope.Project.Value.ToString("N"),
            plan.Id.Value.ToString("N"), part.PartKey.Value)));
    }

    private LocalSinglePartExecutionWriter BuildWriter(IPstArtifactStreamFactory sourceStreamFactory)
    {
        var sourceOptions = new PstStorageOptions { RootPath = _sourceRoot };
        var outputOptions = new PartitionExecutionOutputOptions
        {
            RootPath = _outputRoot,
            MinFreeSpaceMarginBytes = 0,
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new LocalSinglePartExecutionWriter(sourceOptions, outputOptions, new SystemClock(), sourceStreamFactory);
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

    private static byte[] ValidHeaderBytes(int size)
    {
        var bytes = new byte[size];
        // Header PST mínimo aceito pelo pipeline de teste (mesmos bytes mágicos usados em
        // LocalSinglePartExecutionWriterLimitEnforcementTests) — o CONTEÚDO exato não importa para estes
        // testes (a validação de header/versão já é responsabilidade de outro Passo); só o tamanho/hash
        // observado pelo plano precisa corresponder ao que a origem realmente contém no momento do plano.
        bytes[0] = 0x21; bytes[1] = 0x42; bytes[2] = 0x44; bytes[3] = 0x4E;
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

    /// <summary>Abre sempre um NOVO stream a partir do factory fornecido (permite reutilizar o writer entre chamadas).</summary>
    private sealed class FixedStreamFactory(Func<Stream> open) : IPstArtifactStreamFactory
    {
        public Stream OpenRead(string absolutePath) => open();
    }

    /// <summary>
    /// Simula uma origem que muda DE VERDADE enquanto está sendo lida: entrega os bytes originais até
    /// <paramref name="flipAfterByte"/> e, a partir daí, os bytes de <paramref name="mutated"/> na MESMA
    /// posição — como um processo concorrente teria sobrescrito a cauda do arquivo entre duas leituras deste
    /// mesmo worker. Nunca reordena nem duplica bytes; só troca o CONTEÚDO observado a partir do ponto de
    /// corte, preservando o tamanho total (não aciona o ramo de "cresceu além").
    /// </summary>
    private sealed class MidReadMutatingStreamFactory(byte[] original, byte[] mutated, int flipAfterByte) : IPstArtifactStreamFactory
    {
        public Stream OpenRead(string absolutePath) => new MidReadMutatingStream(original, mutated, flipAfterByte);

        private sealed class MidReadMutatingStream(byte[] original, byte[] mutated, int flipAfterByte) : Stream
        {
            private int _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => original.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= original.Length)
                {
                    return ValueTask.FromResult(0);
                }

                var remaining = original.Length - _position;
                var toRead = Math.Min(remaining, buffer.Length);

                // O corte pode cair NO MEIO de um único chunk lido de uma vez (o buffer de streaming do
                // writer é maior que o header sintético usado no teste) — por isso a seleção é feita
                // byte-a-byte dentro do próprio intervalo lido, nunca só uma vez por chamada de Read.
                for (var i = 0; i < toRead; i++)
                {
                    var pos = _position + i;
                    var source = pos >= flipAfterByte ? mutated : original;
                    buffer.Span[i] = source[pos];
                }

                _position += toRead;
                return ValueTask.FromResult(toRead);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
