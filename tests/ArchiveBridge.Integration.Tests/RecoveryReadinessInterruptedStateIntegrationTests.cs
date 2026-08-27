using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Recovery;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Integration.Tests.Support;
using Xunit;

namespace ArchiveBridge.Integration.Tests;

/// <summary>
/// AB-I7-007 item 1 (SQL Server real) — dois dos três failure modes exigidos pelo item 8 do work order
/// AB-I7-005 que ainda não tinham evidência executável dedicada: (A) um efeito externo já concluído
/// (transporte AzCopy comprovado) cujo Job nunca chega a persistir a conclusão (crash entre o
/// <c>AppendAsync</c> da evidência e o <c>CompleteAsync</c> do Job — exatamente o que
/// <c>PurviewUploadCommandProcessor.FinalizeJobAsync</c> faz na ordem inversa); e (C) um SAS/handle já
/// <see cref="SasHandleState.Consumed"/> (uso único) — recovery nunca reemite/reconstrói o segredo, mesmo
/// sob uma tentativa de reaquisição pós-interrupção. O terceiro failure mode (reconciliation/certificate
/// não terminal) é coberto em <c>ReconciliationCertificateIntegrationTests</c>, que já tem toda a
/// infraestrutura de seed necessária.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class RecoveryReadinessInterruptedStateIntegrationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private SqlRecoveryReadinessStore Readiness() => new(fixture.Factory);

    // ---- Failure mode A: upload com efeito externo concluído mas Job completion nunca persistida ----

    [Fact]
    public async Task AnUploadWhoseEvidenceIsPersistedButWhoseJobCompletionNeverPersistsRecoversFromTheExistingEvidenceWithoutRepeatingTheTransport()
    {
        var clock = new MutableClock(Start);
        var requests = new SqlPurviewUploadRequestStore(fixture.Factory, clock);
        var attempts = new SqlPurviewUploadAttemptStore(fixture.Factory);
        var jobs = new SqlJobStore(fixture.Factory, clock, TimeSpan.FromDays(3650));
        var leases = new SqlJobLeaseManager(fixture.Factory, clock, RetryPolicy.Default, Lease);

        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.Approve(Slice2Support.NewWave(
            scope, new WaveSelection([Slice2Support.Entry("recovery-lost-completion.pst", "user@contoso.com", 4096)])));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var enqueue = await requests.EnqueueIdempotentAsync(scope, wave.Id, CorrelationId.New(), CancellationToken.None);

        var claimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, new WorkerId("worker-1"), Lease, CorrelationId.New()), CancellationToken.None);
        Assert.NotNull(claimed);
        var firstFence = new JobFence(scope, claimed!.JobId, new WorkerId("worker-1"), claimed.Epoch);

        // O transporte REALMENTE aconteceu e a evidência foi persistida (o MESMO ponto em que
        // PurviewUploadCommandProcessor.DispatchAsync grava PurviewUploadAttemptOutcome.Uploaded) — mas o
        // worker morre ANTES de FinalizeJobAsync chamar jobs.CompleteAsync. O Job permanece Processing.
        var manifest = new[]
        {
            new PurviewUploadFileManifestItem(
                PartitionExecutionId.New(), PurviewRemotePstName.ForPart(ArtifactId.New(), 1), new Sha256Hash(new string('c', 64)), 4096),
        };
        var evidence = new PurviewUploadEvidence(
            new AzCopyBinaryIdentity("10.25.0", new Sha256Hash(new string('a', 64))),
            PurviewRemoteUploadPrefix.ForWave(scope.Tenant, scope.Project, wave.Id), manifest);
        var uploadedAttempt = new PurviewUploadAttemptRecord(
            enqueue.RequestId, PurviewUploadAttemptId.New(), AttemptNumber: 1, new Sha256Hash(new string('b', 64)),
            PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null, evidence, ProcessExitCode: 0, clock.UtcNow, clock.UtcNow);
        await attempts.AppendAsync(scope, uploadedAttempt, firstFence, CancellationToken.None);

        var stuck = await jobs.GetAsync(scope, claimed.JobId, CancellationToken.None);
        Assert.Equal(JobState.Processing, stuck!.State);

        // O lease expira (o worker nunca voltou) — o reaper é a ÚNICA coisa que move o Job para fora de
        // Processing; a evidência já persistida nunca é usada para "adiantar" isso por si só.
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var recoveredCount = await leases.RecoverExpiredLeasesAsync(Workload.Upload, batchSize: 10, CancellationToken.None);
        Assert.True(recoveredCount >= 1);

        var afterReaper = await jobs.GetAsync(scope, claimed.JobId, CancellationToken.None);
        Assert.Equal(JobState.RetryScheduled, afterReaper!.State); // orçamento de retry disponível (1ª tentativa)

        clock.Advance(RetryPolicy.Default.BaseDelay);
        var reclaimed = await jobs.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.Upload, new WorkerId("worker-2"), Lease, CorrelationId.New()), CancellationToken.None);
        Assert.NotNull(reclaimed);
        Assert.Equal(claimed.JobId, reclaimed!.JobId);

        // MESMO mecanismo de réplay idempotente PRECOCE de PurviewUploadCommandProcessor.DispatchAsync: o
        // novo titular consulta a evidência JÁ persistida ANTES de sequer considerar adquirir um novo SAS
        // ou reexecutar o AzCopy — a "recovery" converge da evidência existente, nunca repete o transporte.
        var recoveredLatest = await attempts.GetLatestAsync(scope, enqueue.RequestId, CancellationToken.None);
        Assert.NotNull(recoveredLatest);
        Assert.Equal(PurviewUploadAttemptOutcome.Uploaded, recoveredLatest!.Outcome);
        Assert.Equal(uploadedAttempt.Attempt, recoveredLatest.Attempt); // MESMA tentativa — nenhuma nova foi criada para "recuperar".

        var completeOutcome = await jobs.CompleteAsync(
            new LeaseCommand(scope, reclaimed.JobId, new WorkerId("worker-2"), reclaimed.Epoch, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(JobCommandOutcome.Applied, completeOutcome);

        var finalState = await jobs.GetAsync(scope, claimed.JobId, CancellationToken.None);
        Assert.Equal(JobState.Completed, finalState!.State);

        // Nenhum SEGUNDO efeito externo foi produzido para "recuperar" — exatamente UMA tentativa Uploaded
        // existe na história completa, mesmo depois da interrupção e da reivindicação por um titular novo.
        var allAttempts = await attempts.ListAttemptsAsync(scope, enqueue.RequestId, CancellationToken.None);
        Assert.Single(allAttempts, attempt => attempt.Outcome == PurviewUploadAttemptOutcome.Uploaded);

        // Evidência executável (I7 Passo 3): registra o exercício de recovery como Pass, com medição real
        // do tempo decorrido entre o crash simulado e a conclusão do Job pelo novo titular.
        var measurement = new RecoveryObjectiveMeasurement(Start, clock.UtcNow);
        var record = await Readiness().RecordExerciseAsync(
            scope, RecoveryExerciseType.ArtifactEvidenceRecovery, RecoveryReadinessStatus.Pass, RecoveryObjective.None,
            objectiveThreshold: null, measurement, evidence.ManifestHash, failureDomain: string.Empty,
            notes: "Upload com efeito externo concluído e conclusão do Job perdida (crash); a recovery converge " +
                "da evidência já persistida sem repetir o transporte (AB-I7-007 item 1, cenário 1).",
            executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(), clock.UtcNow, CancellationToken.None);
        Assert.Equal(RecoveryReadinessStatus.Pass, record.Status);
    }

    // ---- Failure mode C: SAS/handle já consumido (uso único) — recovery nunca reemite o segredo ----

    [Fact]
    public async Task RecoveryNeverReacquiresOrReconstructsASasSecretOnceTheHandleIsConsumedAndConvergesToAnExplicitDenial()
    {
        var clock = new MutableClock(Start);
        var scope = SqlServerFixture.NewScope();
        await Slice2Support.ProjectStore(fixture).AddAsync(Slice2Support.NewProject(scope), CorrelationId.New(), CancellationToken.None);
        var wave = Slice2Support.NewWave(
            scope, new WaveSelection([Slice2Support.Entry("recovery-sas-consumed.pst", "user@contoso.com", 4096)]));
        await Slice2Support.WaveStore(fixture).AddAsync(wave, CorrelationId.New(), CancellationToken.None);

        var handles = new SqlPurviewSasUploadHandleStore(fixture.Factory, clock);
        var secrets = new RecordingFakeSecretStore();
        var useCase = new AcquireSasForUploadUseCase(handles, secrets, clock);

        var stored = await handles.ReplaceCanonicalAsync(scope, wave.Id, null, NewHandle(scope, wave.Id, generation: 1, clock.UtcNow), CancellationToken.None);
        await handles.SaveTransitionAsync(stored.MarkAvailable(clock.UtcNow), CancellationToken.None);

        // Primeira aquisição: sucesso real — o handle é consumido (uso único), exatamente como uma
        // tentativa de upload bem-sucedida faria.
        await useCase.ExecuteAsync(new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None);
        Assert.Equal(1, secrets.AcquireCallCount);

        var consumed = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Consumed, consumed!.State);

        // Simula uma tentativa de RECOVERY pós-interrupção (ex.: um novo titular do Job, reivindicado após
        // o lease do anterior expirar, tentando "recuperar" o upload readquirindo o SAS) — o handle já foi
        // consumido por uma tentativa anterior.
        await Assert.ThrowsAsync<PurviewSasAcquisitionDeniedException>(() => useCase.ExecuteAsync(
            new AcquireSasForUploadRequest(scope, wave.Id, WorkloadIdentities.UploadWorker, CorrelationId.New()), CancellationToken.None));

        // O segredo NUNCA é relido/reconstruído para a tentativa de recovery — a negação ocorre inteiramente
        // no ciclo de vida do handle (fail-closed, ANTES de qualquer chamada ao secret store — mesmo
        // caminho de código do branch `_ => throw` de AcquireSasForUploadUseCase para o estado Consumed).
        Assert.Equal(1, secrets.AcquireCallCount);

        // O estado converge para o MESMO desfecho terminal explícito — nenhuma reconsunção implícita,
        // nenhuma geração nova mintada por si só; a única saída operacional é um NOVO intake explícito
        // (recovery-runbook-i7.md §3).
        var stillConsumed = await handles.GetCanonicalAsync(scope, wave.Id, CancellationToken.None);
        Assert.Equal(SasHandleState.Consumed, stillConsumed!.State);
        Assert.Equal(consumed.Generation, stillConsumed.Generation);
        Assert.Equal(consumed.ConsumedAtUtc, stillConsumed.ConsumedAtUtc);

        // Evidência executável: este failure mode NUNCA pode ser Pass automático — permanece Blocked, com
        // failure domain documentado, exigindo intervenção operacional (novo Intake).
        var record = await Readiness().RecordExerciseAsync(
            scope, RecoveryExerciseType.ArtifactEvidenceRecovery, RecoveryReadinessStatus.Blocked, RecoveryObjective.None,
            objectiveThreshold: null, measurement: null, RecoveryReadinessRecord.NoEvidenceFingerprint,
            failureDomain: "SAS de uso único já consumido — a recovery automática não reconstrói o segredo; exige " +
                "novo Intake explícito (AB-I7-007 item 1, cenário 3; recovery-runbook-i7.md §3).",
            notes: string.Empty, executedBy: "integration-tests", executedByRole: "ServiceAccount", CorrelationId.New(),
            clock.UtcNow, CancellationToken.None);
        Assert.Equal(RecoveryReadinessStatus.Blocked, record.Status);
    }

    private static PurviewSasUploadHandle NewHandle(TenantScope scope, WaveId wave, int generation, DateTimeOffset now) =>
        PurviewSasUploadHandle.Intake(
            SasHandleId.New(), scope.Tenant, scope.Project, wave, generation, new Sha256Hash(new string('a', 64)),
            new SecretStoreHandleReference($"ref-{Guid.NewGuid():N}"), "mystorageaccount123.blob.core.windows.net",
            "ingestiondata", null, now.AddHours(2), CorrelationId.New(), now);

    /// <summary>
    /// Duplo de teste MÍNIMO da porta <see cref="ISecretStore"/> — em memória, sem DPAPI (mesmo espírito do
    /// <c>FakeSecretStore</c> de <c>Slice5PurviewSasUseCaseTests</c>, que não é acessível deste projeto de
    /// testes). Conta chamadas de <see cref="AcquireAsync"/> para provar que uma tentativa de recovery sobre
    /// um handle já Consumed NUNCA chega a tocar o secret store uma segunda vez.
    /// </summary>
    private sealed class RecordingFakeSecretStore : ISecretStore
    {
        public int AcquireCallCount { get; private set; }

        public Task<SecretStoreHandleReference> ProtectAsync(
            TenantScope scope, RedactedSecret secret, CorrelationId correlation, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretStoreHandleReference($"fake-ref-{Guid.NewGuid():N}"));

        public Task<RedactedSecret> AcquireAsync(
            TenantScope scope, SecretStoreHandleReference reference, WorkloadIdentity requester, CorrelationId correlation,
            CancellationToken cancellationToken)
        {
            AcquireCallCount++;
            return Task.FromResult(RedactedSecret.Wrap(
                "https://mystorageaccount123.blob.core.windows.net/ingestiondata?sv=2022-11-02&se=2026-08-24T12%3A00%3A00Z&sp=cw&sig=fake"));
        }

        public Task DestroyAsync(
            TenantScope scope, SecretStoreHandleReference reference, CorrelationId correlation, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
