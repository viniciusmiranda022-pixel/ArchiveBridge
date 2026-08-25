using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Application.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// AB-I5-009 — <see cref="RequestPurviewUploadUseCase"/> e <see cref="PurviewUploadCommandProcessor"/>
/// testáveis só com Domain + Contracts, sem qualquer implementação de Infrastructure (SQL/AzCopy/DPAPI real
/// substituídos por duplos de teste). Prova: onda não elegível recusa o pedido; fonte adulterada/ausente,
/// binário não homologado e SAS negado nunca produzem <c>Uploaded</c>; sucesso persiste evidência e conclui
/// o Job; réplay idempotente (mesma identidade) nunca reexecuta o transporte.
/// </summary>
public sealed class PurviewUploadUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PartitionExecutorIdentity Executor = new("TestExecutor", "1.0");
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));

    private static Sha256Hash Hash(string seed) => DeterministicHash.Compute([seed]);

    private static MigrationWave ApprovedWave()
    {
        var wave = MigrationWave.Create(
            WaveId.New(), Scope.Tenant, Scope.Project, new WaveName("Onda"),
            TargetRootFolder.ForWave(Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N")[..8]),
            Hash("config"), new WaveSelection([]), Now);
        wave.StartValidation();
        wave.MarkReadyForApproval();
        wave.Approve("decision.owner", Now);
        return wave;
    }

    private static (WavePartitionOutputBinding Binding, PartitionExecutionRecord Execution) NewBinding(WaveId wave)
    {
        var planHash = Hash("plan-" + Guid.NewGuid());
        var sourceHash = Hash("source-" + Guid.NewGuid());
        var execution = PartitionExecutionRecord.Complete(
            PartitionExecutionId.New(), Scope.Tenant, Scope.Project, ArtifactId.New(), PartitionPlanId.New(), PartitionPlanPartId.New(),
            planHash, 1, PartitionPlanIdentity.ComputePartKey(planHash, 1), sourceHash, 4096, sourceHash, 4096, Executor,
            CorrelationId.New(), StartedAt, StartedAt.AddSeconds(5));
        var entry = WaveEntryId.Derive(wave, new WaveEntry("C:\\pst\\mailbox.pst", "mailbox.pst", new ArchiveRef("mailbox@contoso.com"), 4096, 10));
        var binding = WavePartitionOutputBinding.Create(
            WavePartitionOutputBindingId.New(), Scope.Tenant, Scope.Project, wave, entry, execution, CorrelationId.New(), Now);
        return (binding, execution);
    }

    private static PurviewSasUploadHandle AvailableSasHandle(WaveId wave, DateTimeOffset expiresAtUtc)
    {
        var handle = PurviewSasUploadHandle.Intake(
            SasHandleId.New(), Scope.Tenant, Scope.Project, wave, generation: 1, Hash("fingerprint"),
            new SecretStoreHandleReference("ref-1"), "acct.blob.core.windows.net", "ingestiondata", keyVersion: null,
            expiresAtUtc, CorrelationId.New(), Now.AddMinutes(-10));
        return handle.MarkAvailable(Now.AddMinutes(-9));
    }

    // ---- RequestPurviewUploadUseCase ----

    [Fact]
    public async Task RequestFailsClosedWhenTheWaveIsStillMutable()
    {
        var wave = MigrationWave.Create(
            WaveId.New(), Scope.Tenant, Scope.Project, new WaveName("Onda"),
            TargetRootFolder.ForWave("p", "w"), Hash("config"), new WaveSelection([]), Now); // Draft.
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var requests = new FakeUploadRequestStore();

        await Assert.ThrowsAsync<PurviewUploadWaveNotEligibleException>(() =>
            new RequestPurviewUploadUseCase(waves, requests)
                .ExecuteAsync(new RequestPurviewUploadRequest(Scope, wave.Id, CorrelationId.New()), CancellationToken.None));
    }

    [Fact]
    public async Task RequestIsIdempotentAcrossTwoCallsForTheSameWave()
    {
        var wave = ApprovedWave();
        var waves = new FakeWaveStore();
        waves.Seed(wave);
        var requests = new FakeUploadRequestStore();
        var useCase = new RequestPurviewUploadUseCase(waves, requests);

        var first = await useCase.ExecuteAsync(new RequestPurviewUploadRequest(Scope, wave.Id, CorrelationId.New()), CancellationToken.None);
        var second = await useCase.ExecuteAsync(new RequestPurviewUploadRequest(Scope, wave.Id, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.True(first.Created);
        Assert.True(second.Replayed);
    }

    // ---- PurviewUploadCommandProcessor ----

    private sealed record Fixture(
        FakeUploadJobStore Jobs, FakeUploadLeaseManager Leases, FakeUploadRequestStore Requests, FakeWaveStore Waves,
        FakeUploadBindingStore Bindings, FakeUploadExecutionStore Executions, FakeVerifier Verifier,
        FakeSasHandleStore SasHandles, FakeSecretStore Secrets, FakeAzCopyExecutor AzCopy, FakeUploadAttemptStore Attempts,
        PurviewUploadCommandProcessor Processor);

    private static Fixture BuildFixture(
        MigrationWave wave, IReadOnlyList<(WavePartitionOutputBinding Binding, PartitionExecutionRecord Execution)> parts,
        AzCopyBinaryIdentity homologatedBinary, PurviewSasUploadHandle? sasHandle)
    {
        var waves = new FakeWaveStore();
        waves.Seed(wave);

        var bindings = new FakeUploadBindingStore([.. parts.Select(p => p.Binding)]);
        var executions = new FakeUploadExecutionStore([.. parts.Select(p => p.Execution)]);
        var verifier = new FakeVerifier();

        var sasHandles = new FakeSasHandleStore();
        if (sasHandle is not null)
        {
            sasHandles.Seed(sasHandle);
        }

        var secrets = new FakeSecretStore(RedactedSecret.Wrap("https://acct.blob.core.windows.net/ingestiondata?sv=x&sig=y&se=" +
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        var sasAcquisition = new AcquireSasForUploadUseCase(sasHandles, secrets, new FixedClock(Now));

        var azcopy = new FakeAzCopyExecutor(homologatedBinary);
        var attempts = new FakeUploadAttemptStore();
        var catalog = new AzCopyHomologationCatalog([homologatedBinary]);

        var requests = new FakeUploadRequestStore();
        var jobId = JobId.New();
        var request = PurviewUploadRequest.Create(PurviewUploadRequestId.New(), Scope.Tenant, Scope.Project, wave.Id, jobId, CorrelationId.New(), Now);
        requests.Seed(request);

        var jobs = new FakeUploadJobStore { ClaimedJobId = jobId };
        var leases = new FakeUploadLeaseManager();

        var processor = new PurviewUploadCommandProcessor(
            jobs, leases, requests, waves, bindings, executions, verifier, sasHandles, sasAcquisition, azcopy, attempts,
            catalog, TimeSpan.FromMinutes(30), new FixedClock(Now));

        return new Fixture(jobs, leases, requests, waves, bindings, executions, verifier, sasHandles, secrets, azcopy, attempts, processor);
    }

    [Fact]
    public async Task ProcessNextReturnsNullWhenThereIsNoWorkToClaim()
    {
        var wave = ApprovedWave();
        var fixture = BuildFixture(wave, [], new AzCopyBinaryIdentity("10.25.0", Hash("bin")), null);
        fixture.Jobs.HasWork = false;

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ANonCanonicalBindingSetFailsClosedAsSourceIntegrityAndFailsTheJobWithoutTouchingAzCopy()
    {
        var wave = ApprovedWave();
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        var fixture = BuildFixture(wave, [], binary, null); // sem bindings.

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(PurviewUploadCommandOutcome.Failed, result!.Outcome);
        Assert.True(fixture.Jobs.FailCalled);
        Assert.Equal(0, fixture.AzCopy.UploadCallCount);
        Assert.Single(fixture.Attempts.Appended);
        Assert.Equal(PurviewUploadAttemptOutcome.SourceIntegrityFailed, fixture.Attempts.Appended[0].Outcome);
    }

    [Fact]
    public async Task APhysicallyTamperedSourceFailsClosedBeforeAnyAzCopyInvocation()
    {
        var wave = ApprovedWave();
        var part = NewBinding(wave.Id);
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        var fixture = BuildFixture(wave, [part], binary, AvailableSasHandle(wave.Id, Now.AddHours(2)));
        fixture.Verifier.ThrowOnVerify = new PartitionExecutionOutputTamperedException("output adulterado (teste)");

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PurviewUploadCommandOutcome.Failed, result!.Outcome);
        Assert.Equal(0, fixture.AzCopy.UploadCallCount);
        Assert.Equal(PurviewUploadAttemptOutcome.SourceIntegrityFailed, fixture.Attempts.Appended[0].Outcome);
    }

    [Fact]
    public async Task ANonHomologatedBinaryFailsClosedBeforeAcquiringTheSas()
    {
        var wave = ApprovedWave();
        var part = NewBinding(wave.Id);
        var homologated = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        var fixture = BuildFixture(wave, [part], homologated, AvailableSasHandle(wave.Id, Now.AddHours(2)));
        fixture.AzCopy.ObservedBinary = new AzCopyBinaryIdentity("10.25.0", Hash("tampered-binary")); // hash divergente.

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PurviewUploadCommandOutcome.Failed, result!.Outcome);
        Assert.Equal(PurviewUploadAttemptOutcome.BinaryMismatch, fixture.Attempts.Appended[0].Outcome);
        Assert.Equal(0, fixture.Secrets.AcquireCallCount); // SAS nunca é sequer tentado.
    }

    [Fact]
    public async Task ASasThatCannotBeAcquiredIsRetriedRatherThanFailed()
    {
        var wave = ApprovedWave();
        var part = NewBinding(wave.Id);
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        // Sem handle SAS custodiado nenhum ⇒ AcquireSasForUploadUseCase recusa fail-closed.
        var fixture = BuildFixture(wave, [part], binary, sasHandle: null);

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PurviewUploadCommandOutcome.Retried, result!.Outcome);
        Assert.True(fixture.Jobs.RetryCalled);
        Assert.Equal(PurviewUploadAttemptOutcome.SasDenied, fixture.Attempts.Appended[0].Outcome);
        Assert.Equal(0, fixture.AzCopy.UploadCallCount);
    }

    [Fact]
    public async Task AProcessFailureIsRetriedAndNeverProducesUploaded()
    {
        var wave = ApprovedWave();
        var part = NewBinding(wave.Id);
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        var fixture = BuildFixture(wave, [part], binary, AvailableSasHandle(wave.Id, Now.AddHours(2)));
        fixture.AzCopy.NextResult = new AzCopyUploadFileResult(ExitCode: 1, TimedOut: false, OutputLimitExceeded: false);

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PurviewUploadCommandOutcome.Retried, result!.Outcome);
        Assert.Equal(PurviewUploadAttemptOutcome.ProcessFailed, fixture.Attempts.Appended[0].Outcome);
        Assert.NotEqual(PurviewUploadAttemptOutcome.Uploaded, fixture.Attempts.Appended[0].Outcome);
    }

    [Fact]
    public async Task ASuccessfulTransportPersistsSanitizedEvidenceAndCompletesTheJob()
    {
        var wave = ApprovedWave();
        var partA = NewBinding(wave.Id);
        var partB = NewBinding(wave.Id);
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        var fixture = BuildFixture(wave, [partA, partB], binary, AvailableSasHandle(wave.Id, Now.AddHours(2)));

        var result = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PurviewUploadCommandOutcome.Completed, result!.Outcome);
        Assert.True(fixture.Jobs.CompleteCalled);
        Assert.Equal(2, fixture.AzCopy.UploadCallCount);
        var attempt = fixture.Attempts.Appended[0];
        Assert.Equal(PurviewUploadAttemptOutcome.Uploaded, attempt.Outcome);
        Assert.NotNull(attempt.Evidence);
        Assert.Equal(2, attempt.Evidence!.ExpectedFileCount);
        Assert.Equal(8192, attempt.Evidence.ExpectedTotalBytes);
        Assert.Equal(binary, attempt.Evidence.Binary);

        // AB-I5-015: a manifestação por arquivo prova, item a item, exatamente QUAIS execuções foram
        // transportadas — nome remoto/hash/tamanho derivados das MESMAS execuções despachadas, nunca de
        // contadores agregados soltos.
        Assert.Equal(2, attempt.Evidence.Manifest.Count);
        foreach (var (binding, execution) in new[] { partA, partB })
        {
            var manifestItem = Assert.Single(attempt.Evidence.Manifest, item => item.Execution == execution.Id);
            Assert.Equal(PurviewRemotePstName.ForPart(execution.Artifact, execution.PartSequence).Value, manifestItem.RemoteName.Value);
            Assert.Equal(execution.OutputHash, manifestItem.OutputHash);
            Assert.Equal(execution.OutputSizeBytes, manifestItem.ExpectedSizeBytes);
            _ = binding; // apenas para desconstrução do par (Binding, Execution).
        }
    }

    [Fact]
    public async Task AnIdempotentReplayWithTheSameIdentityNeverReRunsAzCopy()
    {
        var wave = ApprovedWave();
        var part = NewBinding(wave.Id);
        var binary = new AzCopyBinaryIdentity("10.25.0", Hash("bin"));
        var sasHandle = AvailableSasHandle(wave.Id, Now.AddHours(2));
        var fixture = BuildFixture(wave, [part], binary, sasHandle);

        var first = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);
        Assert.Equal(PurviewUploadCommandOutcome.Completed, first!.Outcome);
        Assert.Equal(1, fixture.AzCopy.UploadCallCount);

        // Segunda "reivindicação" do MESMO Job (ex.: retry/restart do worker) com o MESMO handle/generation
        // e MESMO conjunto de bindings — identidade lógica idêntica (item 14): nunca reexecuta o transporte.
        fixture.Jobs.HasWork = true;
        var second = await fixture.Processor.ProcessNextAsync(
            Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.Equal(PurviewUploadCommandOutcome.Completed, second!.Outcome);
        Assert.Equal(1, fixture.AzCopy.UploadCallCount); // nenhuma nova invocação de AzCopy.
    }

    // ---- Duplos de teste (Domain + Contracts apenas — sem Infrastructure) ----

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeUploadRequestStore : IPurviewUploadRequestStore
    {
        private readonly Dictionary<Guid, PurviewUploadRequest> _byWave = [];
        private readonly Dictionary<Guid, PurviewUploadRequest> _byJob = [];

        public void Seed(PurviewUploadRequest request)
        {
            _byWave[request.Wave.Value] = request;
            _byJob[request.Job.Value] = request;
        }

        public Task<PurviewUploadRequestEnqueueResult> EnqueueIdempotentAsync(
            TenantScope scope, WaveId wave, CorrelationId correlation, CancellationToken cancellationToken)
        {
            if (_byWave.TryGetValue(wave.Value, out var existing))
            {
                return Task.FromResult(new PurviewUploadRequestEnqueueResult(existing.Job, existing.Id, Created: false, Replayed: true));
            }

            var request = PurviewUploadRequest.Create(
                PurviewUploadRequestId.New(), scope.Tenant, scope.Project, wave, JobId.New(), correlation, DateTimeOffset.UtcNow);
            Seed(request);
            return Task.FromResult(new PurviewUploadRequestEnqueueResult(request.Job, request.Id, Created: true, Replayed: false));
        }

        public Task<PurviewUploadRequest?> FindCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken) =>
            Task.FromResult(_byWave.GetValueOrDefault(wave.Value));

        public Task<PurviewUploadRequest?> GetByJobAsync(TenantScope scope, JobId job, CancellationToken cancellationToken) =>
            Task.FromResult(_byJob.GetValueOrDefault(job.Value));
    }

    private sealed class FakeUploadJobStore : IJobStore
    {
        public JobId? ClaimedJobId { get; set; }

        public bool HasWork { get; set; } = true;

        public int AttemptNumber { get; set; } = 1;

        public bool CompleteCalled { get; private set; }

        public bool FailCalled { get; private set; }

        public bool RetryCalled { get; private set; }

        public Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken) => Task.FromResult(JobId.New());

        public Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken)
        {
            if (!HasWork || ClaimedJobId is not { } jobId)
            {
                return Task.FromResult<ClaimedJob?>(null);
            }

            HasWork = false;
            return Task.FromResult<ClaimedJob?>(new ClaimedJob(jobId, new LeaseEpoch(1), DateTimeOffset.UtcNow.AddMinutes(5), AttemptNumber));
        }

        public Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken)
        {
            CompleteCalled = true;
            return Task.FromResult(JobCommandOutcome.Applied);
        }

        public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken)
        {
            FailCalled = true;
            return Task.FromResult(JobCommandOutcome.Applied);
        }

        public Task<JobCommandOutcome> ScheduleRetryAsync(
            LeaseCommand command, ErrorCode errorCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken)
        {
            RetryCalled = true;
            return Task.FromResult(JobCommandOutcome.Applied);
        }

        public Task<JobRetryRequestOutcome> RequestManualRetryAsync(
            TenantScope scope, JobId jobId, Guid idempotencyKey, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUploadLeaseManager : IJobLeaseManager
    {
        public Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> RecoverExpiredLeasesAsync(Workload workload, int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class FakeUploadBindingStore(IReadOnlyList<WavePartitionOutputBinding> bindings) : IWavePartitionOutputBindingStore
    {
        public Task<WavePartitionOutputBinding?> FindCanonicalAsync(
            TenantScope scope, WaveId wave, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WavePartitionOutputBinding>> ListForWaveAsync(
            TenantScope scope, WaveId wave, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WavePartitionOutputBinding>>([.. bindings.Where(binding => binding.Wave == wave)]);

        public Task<WavePartitionOutputBinding> SaveAsync(WavePartitionOutputBinding binding, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUploadExecutionStore(IReadOnlyList<PartitionExecutionRecord> executions) : IPartitionExecutionStore
    {
        public Task<PartitionExecutionRecord?> FindCanonicalAsync(
            TenantScope scope, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken) =>
            Task.FromResult(executions.FirstOrDefault(execution => execution.Plan == plan && execution.Part == part));

        public Task<PartitionExecutionRecord> SaveAsync(PartitionExecutionRecord execution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeVerifier : IPartitionPartVerifier
    {
        public Exception? ThrowOnVerify { get; set; }

        public Task VerifyAsync(TenantScope scope, PartitionExecutionRecord execution, CancellationToken cancellationToken)
        {
            if (ThrowOnVerify is { } exception)
            {
                throw exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeSasHandleStore : IPurviewSasUploadHandleStore
    {
        private PurviewSasUploadHandle? _handle;

        public void Seed(PurviewSasUploadHandle handle) => _handle = handle;

        public Task<PurviewSasUploadHandle?> GetCanonicalAsync(TenantScope scope, WaveId wave, CancellationToken cancellationToken) =>
            Task.FromResult(_handle is { } handle && handle.Tenant == scope.Tenant && handle.Project == scope.Project && handle.Wave == wave
                ? handle
                : null);

        public Task<PurviewSasUploadHandle?> GetByIdAsync(TenantScope scope, SasHandleId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurviewSasUploadHandle> ReplaceCanonicalAsync(
            TenantScope scope, WaveId wave, PurviewSasUploadHandle? expectedPrevious, PurviewSasUploadHandle candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurviewSasUploadHandle> SaveTransitionAsync(PurviewSasUploadHandle handle, CancellationToken cancellationToken)
        {
            _handle = handle;
            return Task.FromResult(handle);
        }
    }

    private sealed class FakeSecretStore(RedactedSecret secret) : ISecretStore
    {
        public int AcquireCallCount { get; private set; }

        public Task<SecretStoreHandleReference> ProtectAsync(
            TenantScope scope, RedactedSecret value, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RedactedSecret> AcquireAsync(
            TenantScope scope, SecretStoreHandleReference reference, WorkloadIdentity requester, CorrelationId correlation,
            CancellationToken cancellationToken)
        {
            AcquireCallCount++;
            return Task.FromResult(secret);
        }

        public Task DestroyAsync(TenantScope scope, SecretStoreHandleReference reference, CorrelationId correlation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAzCopyExecutor(AzCopyBinaryIdentity observedBinary) : IAzCopyUploadExecutor
    {
        public AzCopyBinaryIdentity ObservedBinary { get; set; } = observedBinary;

        public AzCopyUploadFileResult NextResult { get; set; } = new(ExitCode: 0, TimedOut: false, OutputLimitExceeded: false);

        public int UploadCallCount { get; private set; }

        public Task<AzCopyBinaryIdentity> ProbeBinaryAsync(CancellationToken cancellationToken) => Task.FromResult(ObservedBinary);

        public Task<AzCopyUploadFileResult> UploadFileAsync(AzCopyUploadFileRequest request, CancellationToken cancellationToken)
        {
            UploadCallCount++;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeUploadAttemptStore : IPurviewUploadAttemptStore
    {
        public List<PurviewUploadAttemptRecord> Appended { get; } = [];

        public Task AppendAsync(TenantScope scope, PurviewUploadAttemptRecord record, JobFence? fence, CancellationToken cancellationToken)
        {
            Appended.Add(record);
            return Task.CompletedTask;
        }

        public Task<PurviewUploadAttemptRecord?> GetLatestAsync(
            TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken) =>
            Task.FromResult(Appended.Where(record => record.Request == request).OrderByDescending(record => record.AttemptNumber).FirstOrDefault());

        public Task<IReadOnlyList<PurviewUploadAttemptRecord>> ListAttemptsAsync(
            TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PurviewUploadAttemptRecord>>([.. Appended.Where(record => record.Request == request)]);
    }
}
