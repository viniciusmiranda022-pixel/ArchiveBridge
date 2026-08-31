using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Reconciliation;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Tests.Canary;

/// <summary>Duplos de teste do módulo de canário (AB-I8-004) — mesmo padrão de ProductionReadinessTestFakes (AB-I8-001).</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

internal sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
{
    public AuthenticatedActor Current { get; } = new(actorId, roles);
}

internal sealed class UnauthenticatedActorAccessor : IAuthenticatedActorAccessor
{
    public AuthenticatedActor Current => throw new InvalidOperationException("Nenhum principal autenticado válido no contexto atual.");
}

/// <summary>Constrói um Production Readiness Review VIGENTE com todos os 32 controles do catálogo Pass (ReadyForCanary) — mesmo helper de ProductionReadinessGateEvaluatorTests, reaproveitado aqui para testes de canário.</summary>
internal static class ReadyForCanaryReadinessFixture
{
    private static readonly Sha256Hash SomeFingerprint = new(new string('a', 64));

    public static ProductionReadinessReviewSnapshot Build(
        TenantId tenant, ProjectId project, int reviewVersion, string buildCommitSha, CorrelationId correlation, DateTimeOffset now)
    {
        var resolved = new Dictionary<ReadinessControlId, ReadinessControlResult>();
        foreach (var definition in ReadinessControlCatalog.AllControls)
        {
            resolved[definition.Id] = ReadinessControlResult.Create(
                definition.Id, definition.Group, ReadinessControlStatus.Pass,
                ReadinessEvidenceReference.SystemDerived(SomeFingerprint, $"fixture:{definition.Id.Value}"),
                reasonCode: string.Empty, now);
        }

        return ProductionReadinessReviewSnapshot.Compose(
            tenant, project, reviewVersion, buildCommitSha, SomeFingerprint, SomeFingerprint, SomeFingerprint, resolved,
            "svc-readiness", "Administrator", correlation, now);
    }
}

internal sealed class InMemoryProductionReadinessReviewStore : IProductionReadinessReviewStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project), ProductionReadinessReviewSnapshot> _latest = [];

    public void Seed(TenantScope scope, ProductionReadinessReviewSnapshot snapshot) => _latest[(scope.Tenant.Value, scope.Project.Value)] = snapshot;

    public Task<ProductionReadinessReviewSnapshot> RecordReviewAsync(
        TenantScope scope, string buildCommitSha, Sha256Hash buildArtifactDigest, Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint, IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedControlResults,
        string submittedBy, string submittedByRole, CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task<ProductionReadinessReviewSnapshot?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value), out var snapshot) ? snapshot : null);

    public Task<IReadOnlyList<ProductionReadinessReviewSnapshot>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");
}

internal sealed class InMemoryCanaryPlanStore : ICanaryPlanStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project), List<CanaryPlan>> _plans = [];

    public Task<CanaryPlan> AuthorizeAsync(
        TenantScope scope, int readinessReviewVersion, Sha256Hash readinessReviewFingerprint, ProductionReadinessOutcome readinessOutcome,
        string buildCommitSha, Sha256Hash buildArtifactDigest, Sha256Hash policyVersionFingerprint, Sha256Hash capabilityMatrixFingerprint,
        string authorizedBy, string authorizedByRole, CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidate = CanaryPlan.Compose(
            scope.Tenant, scope.Project, CanaryPlanId.New(), planVersion: 1, readinessReviewVersion, readinessReviewFingerprint,
            readinessOutcome, buildCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, authorizedBy,
            authorizedByRole, correlation, now);

        var key = (scope.Tenant.Value, scope.Project.Value);
        if (!_plans.TryGetValue(key, out var list))
        {
            list = [];
            _plans[key] = list;
        }

        var current = list.Count > 0 ? list[^1] : null;
        if (current is not null && string.Equals(current.PlanFingerprint.Value, candidate.PlanFingerprint.Value, StringComparison.Ordinal))
        {
            return Task.FromResult(current);
        }

        var planId = current?.PlanId ?? CanaryPlanId.New();
        var nextVersion = (current?.PlanVersion ?? 0) + 1;
        var record = CanaryPlan.Compose(
            scope.Tenant, scope.Project, planId, nextVersion, readinessReviewVersion, readinessReviewFingerprint, readinessOutcome,
            buildCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint, authorizedBy, authorizedByRole,
            correlation, now);
        list.Add(record);
        return Task.FromResult(record);
    }

    public Task<CanaryPlan?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult(_plans.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null);
    }

    public Task<CanaryPlan?> GetByVersionAsync(TenantScope scope, int planVersion, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult(_plans.TryGetValue(key, out var list) ? list.FirstOrDefault(p => p.PlanVersion == planVersion) : null);
    }

    public Task<IReadOnlyList<CanaryPlan>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        IReadOnlyList<CanaryPlan> result = _plans.TryGetValue(key, out var list) ? [.. list] : [];
        return Task.FromResult(result);
    }
}

internal sealed class InMemoryCanaryScenarioResultStore(InMemoryCanaryPlanStore planStore) : ICanaryScenarioResultStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project, int PlanVersion, string ScenarioId), List<CanaryScenarioResult>> _results = [];

    public async Task<CanaryScenarioResult> RecordResultAsync(
        TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CanaryScenarioStatus status, CanaryEvidenceReference evidence,
        string reasonCode, DateTimeOffset observedAtUtc, string submittedBy, string submittedByRole, CorrelationId correlation,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var latestPlan = await planStore.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);
        if (latestPlan is null || latestPlan.PlanVersion != planVersion)
        {
            throw new CanaryPlanSupersededException("A versão do plano informada já não é a vigente do escopo (fail-closed).");
        }

        var candidate = CanaryScenarioResult.Create(scenarioId, status, evidence, reasonCode, observedAtUtc);
        var candidateFingerprint = CanaryScenarioResult.ComputeContentFingerprint(scenarioId, status, evidence, candidate.ReasonCode, observedAtUtc);

        var key = (scope.Tenant.Value, scope.Project.Value, planVersion, scenarioId.Value);
        if (!_results.TryGetValue(key, out var list))
        {
            list = [];
            _results[key] = list;
        }

        var current = list.Count > 0 ? list[^1] : null;
        if (current is not null)
        {
            var currentFingerprint = CanaryScenarioResult.ComputeContentFingerprint(
                current.ScenarioId, current.Status, current.Evidence, current.ReasonCode, current.ObservedAtUtc);
            if (string.Equals(currentFingerprint.Value, candidateFingerprint.Value, StringComparison.Ordinal))
            {
                return current;
            }
        }

        list.Add(candidate);
        return candidate;
    }

    public Task<CanaryScenarioResult?> GetLatestAsync(TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, planVersion, scenarioId.Value);
        return Task.FromResult(_results.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null);
    }

    public Task<IReadOnlyDictionary<CanaryScenarioId, CanaryScenarioResult>> GetAllLatestForPlanAsync(
        TenantScope scope, int planVersion, CancellationToken cancellationToken)
    {
        var result = new Dictionary<CanaryScenarioId, CanaryScenarioResult>();
        foreach (var (key, list) in _results)
        {
            if (key.Tenant == scope.Tenant.Value && key.Project == scope.Project.Value && key.PlanVersion == planVersion && list.Count > 0)
            {
                result[new CanaryScenarioId(key.ScenarioId)] = list[^1];
            }
        }

        return Task.FromResult<IReadOnlyDictionary<CanaryScenarioId, CanaryScenarioResult>>(result);
    }

    public Task<IReadOnlyList<CanaryScenarioResult>> GetHistoryAsync(
        TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, planVersion, scenarioId.Value);
        IReadOnlyList<CanaryScenarioResult> result = _results.TryGetValue(key, out var list) ? [.. list] : [];
        return Task.FromResult(result);
    }
}

internal sealed class InMemoryMailboxPrecheckStore : IMailboxPrecheckStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project), MailboxPrecheckSnapshot> _latestAcrossMailboxes = [];

    public void Seed(TenantScope scope, MailboxPrecheckSnapshot snapshot) => _latestAcrossMailboxes[(scope.Tenant.Value, scope.Project.Value)] = snapshot;

    public Task<MailboxPrecheckSnapshot?> GetLatestAsync(TenantScope scope, TargetArchiveId mailbox, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task<MailboxPrecheckSnapshot?> GetLatestAcrossMailboxesAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult(_latestAcrossMailboxes.TryGetValue((scope.Tenant.Value, scope.Project.Value), out var snapshot) ? snapshot : null);

    public Task<MailboxPrecheckAppendResult> AppendAsync(MailboxPrecheckSnapshot snapshot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");
}

internal sealed class InMemoryRecoveryReadinessStore : IRecoveryReadinessStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project, RecoveryExerciseType Type), RecoveryReadinessRecord> _latest = [];

    public void Seed(TenantScope scope, RecoveryExerciseType type, RecoveryReadinessRecord record) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, type)] = record;

    public Task<RecoveryReadinessRecord> RecordExerciseAsync(
        TenantScope scope, RecoveryExerciseType exerciseType, RecoveryReadinessStatus status, RecoveryObjective objective,
        TimeSpan? objectiveThreshold, RecoveryObjectiveMeasurement? measurement, Sha256Hash evidenceFingerprint, string failureDomain,
        string notes, string executedBy, string executedByRole, CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task<RecoveryReadinessRecord?> GetLatestAsync(TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, exerciseType), out var record) ? record : null);

    public Task<IReadOnlyList<RecoveryReadinessRecord>> GetHistoryAsync(TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");
}

internal sealed class InMemoryReconciliationCertificateStore : IReconciliationCertificateStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project, Guid Wave, string JobName), ReconciliationCertificate> _latest = [];

    public void Seed(TenantScope scope, WaveId wave, PurviewImportJobName jobName, ReconciliationCertificate certificate) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, wave.Value, jobName.Value)] = certificate;

    public Task<ReconciliationCertificate> IssueOrConvergeAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int assessmentVersion, Sha256Hash assessmentSourceFingerprint,
        Sha256Hash mappingFingerprint, Sha256Hash expectedDecisionsStateFingerprint, ReconciliationOutcome result, int totalItemCount,
        int incompleteItemCount, int deviationCount, Sha256Hash deviationsSha256, bool duplicateRiskDetected, string issuedBy,
        string issuedByRole, CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task<ReconciliationCertificate?> GetLatestAsync(TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, wave.Value, plannedJobName.Value), out var certificate) ? certificate : null);

    public Task<ReconciliationCertificate?> GetByVersionAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int certificateVersion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task<IReadOnlyList<ReconciliationCertificate>> GetHistoryAsync(TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task<ReconciliationCertificate?> GetLatestForWaveAcrossOtherAttemptsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName excludingPlannedJobName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de canário.");

    public Task RecordAuditEventAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int? certificateVersion, ReconciliationCertificateAuditEventType eventType,
        string actorId, string actorRole, bool succeeded, string reason, CorrelationId correlation, DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
