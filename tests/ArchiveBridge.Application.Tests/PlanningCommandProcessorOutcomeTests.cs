using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Application.Planning;
using ArchiveBridge.Application.Waves;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests;

/// <summary>
/// B4: o processador só reporta a transição (Failed/Retried) quando o comando de Job foi de fato
/// APLICADO. Se a época muda (o Job é recuperado/reivindicado) entre a exceção e FailAsync/ScheduleRetry,
/// o comando de Job retorna FencedOut e o desfecho é <see cref="PlanningCommandOutcome.Fenced"/> — nunca
/// uma transição "aplicada".
/// </summary>
public sealed class PlanningCommandProcessorOutcomeTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly ProjectId ProjectId = new(Guid.NewGuid());
    private static readonly TenantScope Scope = new(Tenant, ProjectId);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ProjectConfiguration Config() =>
        new(new TargetTenant("contoso.onmicrosoft.com"), TargetArchivePolicy.OnlineArchive);

    private static MigrationProject Project(Sha256Hash storedHash, ProjectStatus status) =>
        MigrationProject.Rehydrate(
            ProjectId, Tenant, new ProjectName("Projeto"), new ProjectOwner("owner@contoso.com"),
            Config(), ConfigurationVersion.Initial, storedHash, status, Now, Now, RowVersion.None);

    private static PlanningCommandProcessor Processor(
        MigrationProject project, IProjectStore projects, JobCommandOutcome failRetryOutcome)
    {
        var command = PlanningCommandFactory.ValidateProject(project, CorrelationId.New());
        var jobs = new StubJobStore(failRetryOutcome);
        var waves = new UnusedWaveStore();
        var clock = new StubClock(Now);
        return new PlanningCommandProcessor(
            new StubInbox(command),
            jobs,
            new StubLeaseManager(),
            projects,
            waves,
            new ValidateProjectUseCase(projects, clock),
            new ValidateWaveUseCase(waves, new UnusedDecisionStore(), clock),
            new GenerateMappingCsvUseCase(waves, new UnusedMappingStore(), new UnusedArtifactStore(), clock),
            new FreezeWaveUseCase(waves),
            MappingPolicy.Default,
            clock);
    }

    [Fact]
    public async Task TerminalErrorWithFencedFailIsReportedAsFenced()
    {
        // Hash de configuração inconsistente ⇒ erro de domínio terminal; FailAsync cercado ⇒ Fenced.
        var badHash = new Sha256Hash("0000000000000000000000000000000000000000000000000000000000000000");
        var project = Project(badHash, ProjectStatus.Draft);
        var execution = await Processor(project, new StubProjectStore(project, saveThrows: null), JobCommandOutcome.FencedOut)
            .ProcessNextAsync(Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(PlanningCommandOutcome.Fenced, execution!.Outcome);
    }

    [Fact]
    public async Task ConcurrencyWithFencedRetryIsReportedAsFenced()
    {
        // Configuração consistente + SaveStatus lança concorrência; ScheduleRetry cercado ⇒ Fenced.
        var project = Project(Config().ComputeHash(), ProjectStatus.Draft);
        var execution = await Processor(project, new StubProjectStore(project, saveThrows: new ConcurrencyException("x")), JobCommandOutcome.FencedOut)
            .ProcessNextAsync(Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(PlanningCommandOutcome.Fenced, execution!.Outcome);
    }

    [Fact]
    public async Task ConcurrencyWithAppliedRetryIsReportedAsRetried()
    {
        // Contraste: quando ScheduleRetry aplica, o desfecho é Retried (não Fenced).
        var project = Project(Config().ComputeHash(), ProjectStatus.Draft);
        var execution = await Processor(project, new StubProjectStore(project, saveThrows: new ConcurrencyException("x")), JobCommandOutcome.Applied)
            .ProcessNextAsync(Scope, new WorkerId("w"), TimeSpan.FromMinutes(5), CorrelationId.New(), CancellationToken.None);

        Assert.NotNull(execution);
        Assert.Equal(PlanningCommandOutcome.Retried, execution!.Outcome);
    }

    private sealed class StubInbox(PlanningCommand command) : IPlanningCommandInbox
    {
        private bool _served;

        public Task<JobId> EnqueueAsync(PlanningCommand c, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClaimedPlanningCommand?> TryClaimNextAsync(
            TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
        {
            if (_served)
            {
                return Task.FromResult<ClaimedPlanningCommand?>(null);
            }

            _served = true;
            var job = new ClaimedJob(JobId.New(), new LeaseEpoch(1), Now.AddMinutes(5), 1);
            return Task.FromResult<ClaimedPlanningCommand?>(new ClaimedPlanningCommand(job, command));
        }
    }

    private sealed class StubJobStore(JobCommandOutcome failRetryOutcome) : IJobStore
    {
        public Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken) =>
            Task.FromResult(failRetryOutcome);

        public Task<JobCommandOutcome> ScheduleRetryAsync(LeaseCommand command, ErrorCode errorCode, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(failRetryOutcome);
    }

    private sealed class StubLeaseManager : IJobLeaseManager
    {
        public Task<JobCommandOutcome> RenewAsync(LeaseCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(JobCommandOutcome.Applied);

        public Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubProjectStore(MigrationProject project, Exception? saveThrows) : IProjectStore
    {
        public Task AddAsync(MigrationProject p, CorrelationId correlation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MigrationProject?> GetAsync(TenantScope scope, CancellationToken cancellationToken) =>
            Task.FromResult<MigrationProject?>(project);

        public Task SaveStatusAsync(MigrationProject p, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) =>
            saveThrows is not null ? throw saveThrows : Task.CompletedTask;

        public Task SaveConfigurationAsync(MigrationProject p, CorrelationId correlation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveStatusWithApprovalAsync(MigrationProject p, ArchiveBridge.Contracts.Approvals.ApprovalRecord approval, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedWaveStore : IWaveStore
    {
        public Task AddAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MigrationWave?> GetAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveStatusAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) => throw new NotSupportedException();

        public Task SaveValidationAsync(MigrationWave wave, IReadOnlyList<PlanningAssessment> assessments, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null) => throw new NotSupportedException();

        public Task SaveSelectionAsync(MigrationWave wave, CorrelationId correlation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveStatusWithApprovalAsync(MigrationWave wave, ArchiveBridge.Contracts.Approvals.ApprovalRecord approval, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedDecisionStore : ICapacityAssessmentDecisionStore
    {
        public Task RecordAsync(MigrationWave wave, CapacityAssessmentDecision decision, bool unblock, JobFence? fence, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<TargetArchiveId, string>> GetApprovedAsync(TenantScope scope, WaveId waveId, WaveVersion waveVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedMappingStore : IMappingStore
    {
        public Task<int> GetMaxVersionAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MappingCsvVersion?> GetUsableAsync(TenantScope scope, WaveId waveId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MappingReservation?> GetPendingByFingerprintAsync(TenantScope scope, WaveId waveId, MappingGenerationFingerprint fingerprint, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MappingReservation> ReserveAsync(TenantScope scope, MappingGenerationResult result, long expectedSizeBytes, JobFence? fence, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MappingCsvVersion> FinalizeAsync(TenantScope scope, MappingReservation reservation, JobFence? fence, Func<CancellationToken, Task> validatePublishedArtifactAsync, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedArtifactStore : IMappingArtifactStore
    {
        public Task<MappingArtifactStaging> StageAsync(ReadOnlyMemory<byte> csvBytes, Sha256Hash contentSha256, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MappingArtifactReference> PublishAsync(MappingArtifactStaging staging, MappingArtifactDescriptor descriptor, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MappingArtifactContent?> GetAsync(MappingArtifactDescriptor descriptor, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DiscardAsync(MappingArtifactStaging staging, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
