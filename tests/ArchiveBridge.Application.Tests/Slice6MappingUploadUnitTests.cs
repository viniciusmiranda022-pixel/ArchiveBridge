using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Approvals;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// Passo 6A (unitário, sem SQL) — endurecimento do backend de recepção de CSV: teto ABSOLUTO de upload
/// não-parametrizável (§2) e fail-closed com ZERO escrita para identidade/idempotência/RequestedBy/
/// DeclaredLength inválidos (§4/§5/§6). Os fakes provam que nem o stream é lido nem a custódia é chamada.
/// </summary>
public sealed class Slice6MappingUploadUnitTests
{
    private const long FiftyMib = 50L * 1024L * 1024L;

    // ===================== §2 teto ABSOLUTO de upload =====================

    [Fact]
    public void AbsoluteMaxUploadBytesIsFiftyMibConstant() =>
        Assert.Equal(FiftyMib, MappingUploadLimits.AbsoluteMaxUploadBytes);

    [Fact]
    public void EffectiveAtAbsoluteCeilingIsAllowed()
    {
        var limits = new MappingUploadLimits(effectiveMaxUploadBytes: FiftyMib);
        Assert.Equal(FiftyMib, limits.EffectiveMaxUploadBytes);
    }

    [Theory]
    [InlineData(FiftyMib + 1)] // acima do teto absoluto
    [InlineData(0L)]           // não positivo
    [InlineData(-1L)]          // negativo
    [InlineData(long.MinValue)]
    public void EffectiveOutsideRangeIsRejected(long effective) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MappingUploadLimits(effectiveMaxUploadBytes: effective));

    [Fact]
    public void DefaultLimitsAreWithinAbsoluteCeiling() =>
        Assert.True(MappingUploadLimits.Default.EffectiveMaxUploadBytes <= MappingUploadLimits.AbsoluteMaxUploadBytes);

    // ===================== §5 identidade/idempotência — zero writes =====================

    [Fact]
    public async Task EmptyUserIdThrowsAndNeverTouchesStoreOrStream()
    {
        var store = new ThrowIfUsedValidationStore();
        var stream = new ThrowIfReadStream();
        await Assert.ThrowsAsync<ArgumentException>(() => UseCase(store).ExecuteAsync(
            Request(stream, userId: Guid.Empty), MappingPolicy.Default, CancellationToken.None));
        Assert.False(store.PersistCalled);
        Assert.False(stream.ReadCalled);
    }

    [Fact]
    public async Task EmptyIdempotencyKeyThrowsAndNeverTouchesStoreOrStream()
    {
        var store = new ThrowIfUsedValidationStore();
        var stream = new ThrowIfReadStream();
        await Assert.ThrowsAsync<ArgumentException>(() => UseCase(store).ExecuteAsync(
            Request(stream, key: Guid.Empty), MappingPolicy.Default, CancellationToken.None));
        Assert.False(store.PersistCalled);
        Assert.False(stream.ReadCalled);
    }

    // ===================== §6 RequestedBy — obrigatório/normalizado, zero writes =====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]           // só espaços ⇒ vazio após Trim
    [InlineData("ab")]        // caractere de controle (BEL) no meio
    [InlineData("line\nbreak")]   // caractere de controle (LF)
    public async Task InvalidRequestedByThrowsAndNeverTouchesStoreOrStream(string? requestedBy)
    {
        var store = new ThrowIfUsedValidationStore();
        var stream = new ThrowIfReadStream();
        await Assert.ThrowsAsync<ArgumentException>(() => UseCase(store).ExecuteAsync(
            Request(stream, requestedBy: requestedBy!), MappingPolicy.Default, CancellationToken.None));
        Assert.False(store.PersistCalled);
        Assert.False(stream.ReadCalled);
    }

    [Fact]
    public async Task RequestedByOver200CharsIsRejected()
    {
        var store = new ThrowIfUsedValidationStore();
        var stream = new ThrowIfReadStream();
        await Assert.ThrowsAsync<ArgumentException>(() => UseCase(store).ExecuteAsync(
            Request(stream, requestedBy: new string('x', 201)), MappingPolicy.Default, CancellationToken.None));
        Assert.False(store.PersistCalled);
    }

    // ===================== §4 DeclaredLength negativo — fail-closed pré-leitura =====================

    [Fact]
    public async Task NegativeDeclaredLengthIsRejectedWithoutReadingStreamOrPersisting()
    {
        var store = new ThrowIfUsedValidationStore();
        var stream = new ThrowIfReadStream();
        await Assert.ThrowsAsync<MappingUploadRejectedException>(() => UseCase(store).ExecuteAsync(
            Request(stream, declaredLength: -1), MappingPolicy.Default, CancellationToken.None));
        Assert.False(stream.ReadCalled); // stream NÃO lido
        Assert.False(store.PersistCalled); // custódia NÃO chamada
    }

    // ===================== helpers =====================

    private static ValidateMappingCsvUploadUseCase UseCase(IMappingValidationStore store) =>
        new(new ThrowIfUsedWaveStore(), store, new FixedClock(), MappingUploadLimits.Default);

    private static MappingCsvUploadRequest Request(
        Stream content,
        Guid? userId = null,
        Guid? key = null,
        string requestedBy = "operator",
        long? declaredLength = null) =>
        new(
            new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid())),
            WaveId.New(),
            new ContentCodePage(1252),
            content,
            "mapping.csv",
            declaredLength,
            userId ?? Guid.NewGuid(),
            requestedBy,
            key ?? Guid.NewGuid(),
            CorrelationId.New());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class ThrowIfReadStream : Stream
    {
        public bool ReadCalled { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalled = true;
            throw new InvalidOperationException("O stream não deveria ter sido lido (fail-closed pré-leitura).");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalled = true;
            throw new InvalidOperationException("O stream não deveria ter sido lido (fail-closed pré-leitura).");
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowIfUsedValidationStore : IMappingValidationStore
    {
        public bool PersistCalled { get; private set; }

        public Task<MappingValidationPersistResult> PersistAsync(MappingValidationAttempt attempt, CancellationToken cancellationToken)
        {
            PersistCalled = true;
            throw new InvalidOperationException("A custódia não deveria ter sido chamada (fail-closed, zero writes).");
        }

        public Task<MappingValidationAttempt?> GetAsync(TenantScope scope, Guid validationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("GetAsync não deveria ter sido chamado.");

        public Task<MappingValidationAttempt?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("GetLatestAsync não deveria ter sido chamado.");
    }

    private sealed class ThrowIfUsedWaveStore : IWaveStore
    {
        public Task AddAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("IWaveStore não deveria ter sido chamado (fail-closed antes do lookup).");

        public Task<MigrationWave?> GetAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("IWaveStore não deveria ter sido chamado (fail-closed antes do lookup).");

        public Task SaveStatusAsync(
            MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) =>
            throw new InvalidOperationException("não esperado");

        public Task SaveValidationAsync(
            MigrationWave wave,
            IReadOnlyList<PlanningAssessment> assessments,
            CorrelationId correlation,
            CancellationToken cancellationToken,
            JobFence? fence = null) =>
            throw new InvalidOperationException("não esperado");

        public Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("não esperado");

        public Task SaveStatusWithApprovalAsync(MigrationWave wave, ApprovalRecord approval, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("não esperado");
    }
}
