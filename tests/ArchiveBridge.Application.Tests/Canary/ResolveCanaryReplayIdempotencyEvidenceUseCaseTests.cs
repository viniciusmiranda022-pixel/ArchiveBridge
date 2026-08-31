using ArchiveBridge.Application.Canary;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;
using Xunit;

namespace ArchiveBridge.Application.Tests.Canary;

/// <summary>
/// AB-I8-006 — <see cref="ResolveCanaryReplayIdempotencyEvidenceUseCase"/>: CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT
/// reclassificado de OperatorAttested para SystemDerived. Pass exige DUAS provas reais — réplay de fato
/// observado (mais de uma tentativa despachada) E exatamente uma tentativa Uploaded (nenhum efeito
/// duplicado) — nunca o status alegado pelo operador.
/// </summary>
public sealed class ResolveCanaryReplayIdempotencyEvidenceUseCaseTests
{
    private static readonly TenantScope Scope = new(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly Sha256Hash SomeHash = new(new string('a', 64));
    private static readonly WaveId Wave = WaveId.New();

    private static async Task<(InMemoryCanaryScenarioResultStore ResultStore, InMemoryPurviewUploadRequestStore RequestStore, InMemoryPurviewUploadAttemptStore AttemptStore)> SeedAuthorizedPlanAsync()
    {
        var planStore = new InMemoryCanaryPlanStore();
        await planStore.AuthorizeAsync(
            Scope, 1, SomeHash, Domain.ProductionReadiness.ProductionReadinessOutcome.ReadyForCanary,
            "0123456789abcdef0123456789abcdef01234567", SomeHash, SomeHash, SomeHash, "approver-1", "Approver",
            CorrelationId.New(), Now, CancellationToken.None);
        return (new InMemoryCanaryScenarioResultStore(planStore), new InMemoryPurviewUploadRequestStore(), new InMemoryPurviewUploadAttemptStore());
    }

    private static ResolveCanaryReplayIdempotencyEvidenceUseCase BuildUseCase(
        InMemoryPurviewUploadRequestStore requestStore, InMemoryPurviewUploadAttemptStore attemptStore, InMemoryCanaryScenarioResultStore resultStore) =>
        new(requestStore, attemptStore, resultStore, new FixedClock(Now), new FakeAuthenticatedActorAccessor("operator-1", PortalRoles.Operator));

    private static PurviewUploadRequest SeedRequest(InMemoryPurviewUploadRequestStore requestStore)
    {
        var request = PurviewUploadRequest.Create(PurviewUploadRequestId.New(), Scope.Tenant, Scope.Project, Wave, JobId.New(), CorrelationId.New(), Now);
        requestStore.Seed(Scope, request);
        return request;
    }

    private static PurviewUploadAttemptRecord UploadedAttempt(PurviewUploadRequestId request, int attemptNumber, Sha256Hash identity) =>
        new(request, PurviewUploadAttemptId.New(), attemptNumber, identity, PurviewUploadAttemptOutcome.Uploaded, BlockingReason: null,
            Evidence: null, ProcessExitCode: 0, Now, Now);

    private static PurviewUploadAttemptRecord FailedAttempt(PurviewUploadRequestId request, int attemptNumber) =>
        new(request, PurviewUploadAttemptId.New(), attemptNumber, new Sha256Hash($"unresolved:{attemptNumber}"), PurviewUploadAttemptOutcome.SasDenied,
            "SAS_ACQUISITION_DENIED", Evidence: null, ProcessExitCode: null, Now, Now);

    [Fact]
    public async Task WithNoUploadRequestTheScenarioIsNotPerformed()
    {
        var (resultStore, requestStore, attemptStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(requestStore, attemptStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanaryReplayIdempotencyEvidenceCommand(Scope, 1, Wave, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
        Assert.Equal("UPLOAD_REQUEST_NOT_YET_CREATED", result.ReasonCode);
    }

    [Fact]
    public async Task WithARequestButNoAttemptsTheScenarioIsNotPerformed()
    {
        var (resultStore, requestStore, attemptStore) = await SeedAuthorizedPlanAsync();
        SeedRequest(requestStore);
        var useCase = BuildUseCase(requestStore, attemptStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanaryReplayIdempotencyEvidenceCommand(Scope, 1, Wave, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.NotPerformed, result.Status);
        Assert.Equal("UPLOAD_NOT_YET_COMPLETED", result.ReasonCode);
    }

    [Fact]
    public async Task ASingleIsolatedUploadedAttemptIsBlockedNeverPass()
    {
        var (resultStore, requestStore, attemptStore) = await SeedAuthorizedPlanAsync();
        var request = SeedRequest(requestStore);
        attemptStore.Seed(request.Id, UploadedAttempt(request.Id, attemptNumber: 1, SomeHash));
        var useCase = BuildUseCase(requestStore, attemptStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanaryReplayIdempotencyEvidenceCommand(Scope, 1, Wave, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Blocked, result.Status);
        Assert.Equal("REPLAY_NOT_YET_OBSERVED", result.ReasonCode);
    }

    [Fact]
    public async Task ARetriedThenUploadedAttemptProvesRealReplayConvergenceAndIsPass()
    {
        var (resultStore, requestStore, attemptStore) = await SeedAuthorizedPlanAsync();
        var request = SeedRequest(requestStore);
        attemptStore.Seed(request.Id, FailedAttempt(request.Id, attemptNumber: 1), UploadedAttempt(request.Id, attemptNumber: 2, SomeHash));
        var useCase = BuildUseCase(requestStore, attemptStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanaryReplayIdempotencyEvidenceCommand(Scope, 1, Wave, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Pass, result.Status);
        Assert.Equal(CanaryEvidenceKind.SystemDerived, result.Evidence.Kind);
    }

    [Fact]
    public async Task MoreThanOneUploadedAttemptIsAStructuralFailureNeverPass()
    {
        var (resultStore, requestStore, attemptStore) = await SeedAuthorizedPlanAsync();
        var request = SeedRequest(requestStore);
        attemptStore.Seed(
            request.Id,
            UploadedAttempt(request.Id, attemptNumber: 1, SomeHash),
            UploadedAttempt(request.Id, attemptNumber: 2, new Sha256Hash(new string('b', 64))));
        var useCase = BuildUseCase(requestStore, attemptStore, resultStore);

        var result = await useCase.ExecuteAsync(new ResolveCanaryReplayIdempotencyEvidenceCommand(Scope, 1, Wave, CorrelationId.New()), CancellationToken.None);

        Assert.Equal(CanaryScenarioStatus.Fail, result.Status);
        Assert.Equal("MULTIPLE_UPLOADED_ATTEMPTS_STRUCTURALLY_UNEXPECTED", result.ReasonCode);
    }

    [Fact]
    public async Task ResolvedScenarioIsPersistedAndReadableAfterward()
    {
        var (resultStore, requestStore, attemptStore) = await SeedAuthorizedPlanAsync();
        var useCase = BuildUseCase(requestStore, attemptStore, resultStore);
        await useCase.ExecuteAsync(new ResolveCanaryReplayIdempotencyEvidenceCommand(Scope, 1, Wave, CorrelationId.New()), CancellationToken.None);

        var persisted = await resultStore.GetLatestAsync(Scope, 1, new CanaryScenarioId("CANARY.REPLAY_SAME_TARGET_ROOT_IDEMPOTENT"), CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(CanaryScenarioStatus.NotPerformed, persisted!.Status);
    }
}
