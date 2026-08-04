using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Mapping;
using ArchiveBridge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// R5-B2: a publicação do artefato NÃO ocorre sob transação SQL. O protocolo é: StageAsync (fora do SQL)
/// → ReserveAsync (transação 1 curta) → PublishAsync (fora do SQL) → FinalizeAsync (transação 2 curta,
/// validando o bundle ANTES de abrir a transação). Prova-se que, enquanto a publicação (I/O de
/// filesystem/SMB/NAS) está em curso, NENHUMA transação SQL está aberta — o lock da linha do Job é
/// adquirível e outras consultas SQL funcionam — e que uma queda entre as fases é reconciliável pela
/// impressão digital, sem gerar versão indevida. Um worker defasado (lease vencido) não finaliza.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class Slice2PublishOutsideTxTests(SqlServerFixture fixture)
{
    private static readonly WorkerId WorkerA = new("worker-A");

    private async Task<(TenantScope Scope, WaveId WaveId)> ApprovedWaveAsync()
    {
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(scope, new WaveSelection([Slice2Support.Entry("a.pst", "u@contoso.com", 10)])));
        var store = Slice2Support.WaveStore(fixture);
        await store.AddAsync(wave, CorrelationId.New(), CancellationToken.None);
        await store.SaveStatusAsync(wave, CorrelationId.New(), CancellationToken.None);
        return (scope, wave.Id);
    }

    private async Task<MappingGenerationResult> GenerateAsync(TenantScope scope, WaveId waveId, int codePage, MutableClock clock)
    {
        var wave = await Slice2Support.WaveStore(fixture, clock).GetAsync(scope, waveId, CancellationToken.None);
        return MappingCsvGenerator.Generate(wave!, new ContentCodePage(codePage), MappingPolicy.Default, MappingVersion.Initial, "do", clock.UtcNow);
    }

    private static Func<CancellationToken, Task> Validate(FileSystemMappingArtifactStore artifacts, MappingArtifactDescriptor descriptor) =>
        async token => _ = await artifacts.GetAsync(descriptor, token)
            ?? throw new MappingGenerationException("Artefato publicado ausente na finalização (fail-closed).");

    [Fact]
    public async Task PublishRunsWithNoSqlTransactionOpen()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, waveId) = await ApprovedWaveAsync();

        // Fence de um Job real reivindicado (para provar que a guarda de fencing NÃO fica aberta no publish).
        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            PlanningCommandFactory.GenerateMappingCsv(
                (await Slice2Support.WaveStore(fixture, clock).GetAsync(scope, waveId, CancellationToken.None))!,
                new ContentCodePage(1252), "do", MappingPolicy.Default, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, WorkerA, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);
        var fence = new JobFence(scope, claimed!.Job.JobId, WorkerA, claimed.Job.Epoch);

        var result = await GenerateAsync(scope, waveId, 1252, clock);
        var inner = Slice2Support.ArtifactStore(fixture);
        var gated = new GatedArtifactStore(inner);
        var mappings = Slice2Support.MappingStore(fixture, clock);

        var staging = await gated.StageAsync(result.Document.GetBytes(), result.Document.ContentSha256, CancellationToken.None);
        var reservation = await mappings.ReserveAsync(scope, result, staging.SizeBytes, fence, CancellationToken.None); // transação 1 commitada
        var descriptor = new MappingArtifactDescriptor(scope, waveId, reservation.Version);

        // Publica em segundo plano, bloqueando no meio da fase de filesystem.
        var publishTask = gated.PublishAsync(staging, descriptor, CancellationToken.None);
        await gated.PublishStarted.Task; // publicação em andamento (bloqueada)

        // PROVA 1: nenhuma transação de fencing aberta ⇒ o lock da linha do Job é adquirível (sem 1222).
        await using (var reaper = await fixture.Factory.OpenForMaintenanceAsync(CancellationToken.None))
        await using (var lockCmd = new SqlCommand(
            "SET LOCK_TIMEOUT 4000; SELECT 1 FROM dbo.jobs WITH (UPDLOCK) WHERE job_id = @j;", reaper.Connection))
        {
            lockCmd.Parameters.AddWithValue("@j", claimed.Job.JobId.Value);
            var acquired = await lockCmd.ExecuteScalarAsync(CancellationToken.None);
            Assert.Equal(1, acquired); // adquiriu o lock: a guarda de fencing não está retida durante o publish
        }

        // PROVA 2: outra consulta SQL funciona (sem lock de tabela retido durante o publish).
        Assert.Equal(reservation.Version.Value, await mappings.GetMaxVersionAsync(scope, waveId, CancellationToken.None));

        // Libera a publicação e finaliza (transação 2).
        gated.ReleasePublish.TrySetResult();
        await publishTask;
        var persisted = await mappings.FinalizeAsync(scope, reservation, fence, Validate(inner, descriptor), CancellationToken.None);

        Assert.Equal(MappingVersionStatus.Usable, persisted.Status);
        var usable = await mappings.GetUsableAsync(scope, waveId, CancellationToken.None);
        Assert.Equal(reservation.Version.Value, usable!.Version.Value);
    }

    [Fact]
    public async Task CrashAfterReserveBeforePublishRecoversSameVersion()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, waveId) = await ApprovedWaveAsync();
        var result = await GenerateAsync(scope, waveId, 1252, clock);
        var artifacts = Slice2Support.ArtifactStore(fixture);
        var mappings = Slice2Support.MappingStore(fixture, clock);

        // Reserva (transação 1) e depois "cai": o staging é perdido, nada é publicado nem finalizado.
        var staging = await artifacts.StageAsync(result.Document.GetBytes(), result.Document.ContentSha256, CancellationToken.None);
        _ = await mappings.ReserveAsync(scope, result, staging.SizeBytes, fence: null, CancellationToken.None);
        await artifacts.DiscardAsync(staging, CancellationToken.None);

        Assert.Null(await mappings.GetUsableAsync(scope, waveId, CancellationToken.None)); // ainda não utilizável
        Assert.Equal(1, await mappings.GetMaxVersionAsync(scope, waveId, CancellationToken.None)); // reserva conta

        // Retry pela MESMA geração: reconcilia a reserva pendente (não cria versão nova) e finaliza.
        var retry = await Slice2Support.GenerateMapping(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, waveId, new ContentCodePage(1252), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);

        Assert.Equal(1, retry.Version.Version.Value); // recuperou a MESMA versão, não a 2
        Assert.Equal(1, await mappings.GetMaxVersionAsync(scope, waveId, CancellationToken.None)); // nenhuma versão indevida
        var usable = await mappings.GetUsableAsync(scope, waveId, CancellationToken.None);
        Assert.Equal(1, usable!.Version.Value);
    }

    [Fact]
    public async Task CrashAfterPublishBeforeFinalizeRecovers()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, waveId) = await ApprovedWaveAsync();
        var result = await GenerateAsync(scope, waveId, 1252, clock);
        var artifacts = Slice2Support.ArtifactStore(fixture);
        var mappings = Slice2Support.MappingStore(fixture, clock);

        // Reserva + publica e depois "cai" antes de finalizar (linha pendente + artefato publicado).
        var staging = await artifacts.StageAsync(result.Document.GetBytes(), result.Document.ContentSha256, CancellationToken.None);
        var reservation = await mappings.ReserveAsync(scope, result, staging.SizeBytes, fence: null, CancellationToken.None);
        await artifacts.PublishAsync(staging, new MappingArtifactDescriptor(scope, waveId, reservation.Version), CancellationToken.None);

        Assert.Null(await mappings.GetUsableAsync(scope, waveId, CancellationToken.None)); // pendente, não utilizável

        var retry = await Slice2Support.GenerateMapping(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, waveId, new ContentCodePage(1252), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);

        Assert.Equal(1, retry.Version.Version.Value);
        var usable = await mappings.GetUsableAsync(scope, waveId, CancellationToken.None);
        Assert.Equal(1, usable!.Version.Value);
    }

    [Fact]
    public async Task PreviousVersionStaysUsableUntilNewOneFinalized()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, waveId) = await ApprovedWaveAsync();
        var artifacts = Slice2Support.ArtifactStore(fixture);
        var mappings = Slice2Support.MappingStore(fixture, clock);

        // v1 finalizada (utilizável).
        await Slice2Support.GenerateMapping(fixture, clock)
            .ExecuteAsync(scope, waveId, new ContentCodePage(1252), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);

        // v2 (outra code page) apenas RESERVADA (pendente): a v1 permanece utilizável.
        var result2 = await GenerateAsync(scope, waveId, 65001, clock);
        var staging2 = await artifacts.StageAsync(result2.Document.GetBytes(), result2.Document.ContentSha256, CancellationToken.None);
        _ = await mappings.ReserveAsync(scope, result2, staging2.SizeBytes, fence: null, CancellationToken.None);

        var usableDuringPending = await mappings.GetUsableAsync(scope, waveId, CancellationToken.None);
        Assert.Equal(1, usableDuringPending!.Version.Value); // v1 continua utilizável enquanto v2 pendente
        Assert.Equal(2, await mappings.GetMaxVersionAsync(scope, waveId, CancellationToken.None));
        await artifacts.DiscardAsync(staging2, CancellationToken.None);

        // Retry da geração 65001: reconcilia a v2 pendente e a finaliza (agora v1 vira Superseded).
        var retry = await Slice2Support.GenerateMapping(fixture, new MutableClock(Slice2Support.Now))
            .ExecuteAsync(scope, waveId, new ContentCodePage(65001), MappingPolicy.Default, "do", CorrelationId.New(), CancellationToken.None);

        Assert.Equal(2, retry.Version.Version.Value);
        var usable = await mappings.GetUsableAsync(scope, waveId, CancellationToken.None);
        Assert.Equal(2, usable!.Version.Value);
    }

    [Fact]
    public async Task StaleWorkerCannotFinalizeReservation()
    {
        var clock = new MutableClock(Slice2Support.Now);
        var (scope, waveId) = await ApprovedWaveAsync();

        await Slice2Support.Inbox(fixture, clock).EnqueueAsync(
            PlanningCommandFactory.GenerateMappingCsv(
                (await Slice2Support.WaveStore(fixture, clock).GetAsync(scope, waveId, CancellationToken.None))!,
                new ContentCodePage(1252), "do", MappingPolicy.Default, CorrelationId.New()),
            CancellationToken.None);
        var claimed = await Slice2Support.Inbox(fixture, clock)
            .TryClaimNextAsync(scope, WorkerA, Slice2Support.Lease, CorrelationId.New(), CancellationToken.None);
        Assert.NotNull(claimed);
        var fence = new JobFence(scope, claimed!.Job.JobId, WorkerA, claimed.Job.Epoch);

        var result = await GenerateAsync(scope, waveId, 1252, clock);
        var artifacts = Slice2Support.ArtifactStore(fixture);
        var mappings = Slice2Support.MappingStore(fixture, clock);

        var staging = await artifacts.StageAsync(result.Document.GetBytes(), result.Document.ContentSha256, CancellationToken.None);
        var reservation = await mappings.ReserveAsync(scope, result, staging.SizeBytes, fence, CancellationToken.None);
        var descriptor = new MappingArtifactDescriptor(scope, waveId, reservation.Version);
        await artifacts.PublishAsync(staging, descriptor, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(7)); // lease vencido: worker defasado

        await Assert.ThrowsAsync<FencedOutException>(() =>
            mappings.FinalizeAsync(scope, reservation, fence, Validate(artifacts, descriptor), CancellationToken.None));

        Assert.Null(await mappings.GetUsableAsync(scope, waveId, CancellationToken.None)); // nunca promovida
        Assert.NotNull(await mappings.GetPendingByFingerprintAsync(scope, waveId, reservation.Fingerprint, CancellationToken.None)); // reserva preservada
    }

    /// <summary>Artefato instrumentado: a publicação sinaliza o início e bloqueia até liberação explícita.</summary>
    private sealed class GatedArtifactStore(IMappingArtifactStore inner) : IMappingArtifactStore
    {
        public TaskCompletionSource PublishStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MappingArtifactStaging> StageAsync(ReadOnlyMemory<byte> csvBytes, Sha256Hash contentSha256, CancellationToken cancellationToken) =>
            inner.StageAsync(csvBytes, contentSha256, cancellationToken);

        public async Task<MappingArtifactReference> PublishAsync(MappingArtifactStaging staging, MappingArtifactDescriptor descriptor, CancellationToken cancellationToken)
        {
            PublishStarted.TrySetResult();
            await ReleasePublish.Task.ConfigureAwait(false);
            return await inner.PublishAsync(staging, descriptor, cancellationToken).ConfigureAwait(false);
        }

        public Task<MappingArtifactContent?> GetAsync(MappingArtifactDescriptor descriptor, CancellationToken cancellationToken) =>
            inner.GetAsync(descriptor, cancellationToken);

        public Task DiscardAsync(MappingArtifactStaging staging, CancellationToken cancellationToken) =>
            inner.DiscardAsync(staging, cancellationToken);
    }
}
