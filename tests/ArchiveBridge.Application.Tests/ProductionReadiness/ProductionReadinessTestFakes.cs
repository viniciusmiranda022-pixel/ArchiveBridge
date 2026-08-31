using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Contracts.Recovery;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Recovery;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Tests.ProductionReadiness;

/// <summary>Relógio fixo determinístico para os testes do Production Readiness Review.</summary>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Ator autenticado fake — identidade/papéis controlados pelo teste, nunca pelo comando (mesmo princípio de produção).</summary>
internal sealed class FakeAuthenticatedActorAccessor(string actorId, params string[] roles) : IAuthenticatedActorAccessor
{
    public AuthenticatedActor Current { get; } = new(actorId, roles);
}

/// <summary>Ator não autenticado — <see cref="Current"/> lança, mesma semântica fail-closed da implementação de produção.</summary>
internal sealed class UnauthenticatedActorAccessor : IAuthenticatedActorAccessor
{
    public AuthenticatedActor Current => throw new InvalidOperationException("Nenhum principal autenticado válido no contexto atual.");
}

/// <summary>Store em memória do <see cref="PenTestReadinessBundle"/> — sem SQL, só para orquestração/agregação.</summary>
internal sealed class InMemoryPenTestReadinessStore : IPenTestReadinessStore
{
    private readonly Dictionary<(Guid, Guid), PenTestReadinessBundle> _latest = [];

    public void Seed(TenantScope scope, PenTestReadinessBundle bundle) => _latest[(scope.Tenant.Value, scope.Project.Value)] = bundle;

    public Task<PenTestReadinessBundle> RecordBundleAsync(
        TenantScope scope, PenTestReadinessStatus status, string scopeSummary, string attackSurfaceSummary,
        string trustBoundariesSummary, string syntheticFixturesDescription, string knownBlockedItemsSummary,
        Sha256Hash targetBuildDigest, string blockedReason, string preparedBy, string preparedByRole,
        CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<PenTestReadinessBundle?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value), out var bundle) ? bundle : null);
}

/// <summary>Store em memória do <see cref="WorkerHardeningControlRecord"/>.</summary>
internal sealed class InMemoryWorkerHardeningBaselineStore : IWorkerHardeningBaselineStore
{
    private readonly Dictionary<(Guid, Guid, WorkerHardeningControl), WorkerHardeningControlRecord> _latest = [];

    public void Seed(TenantScope scope, WorkerHardeningControlRecord record) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, record.Control)] = record;

    public Task<WorkerHardeningControlRecord> RecordControlAsync(
        TenantScope scope, WorkerHardeningControl control, WorkerHardeningStatus status, WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint, string blockedReason, string notes, string executedBy, string executedByRole,
        CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<WorkerHardeningControlRecord?> GetLatestAsync(TenantScope scope, WorkerHardeningControl control, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, control), out var record) ? record : null);

    public Task<IReadOnlyList<WorkerHardeningControlRecord>> GetLatestForAllControlsAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkerHardeningControlRecord>>(
            [.. _latest.Where(entry => entry.Key.Item1 == scope.Tenant.Value && entry.Key.Item2 == scope.Project.Value).Select(entry => entry.Value)]);
}

/// <summary>Store em memória do <see cref="WdacPolicyEvidence"/>.</summary>
internal sealed class InMemoryWdacPolicyEvidenceStore : IWdacPolicyEvidenceStore
{
    private readonly Dictionary<(Guid, Guid), WdacPolicyEvidence> _latest = [];

    public void Seed(TenantScope scope, WdacPolicyEvidence evidence) => _latest[(scope.Tenant.Value, scope.Project.Value)] = evidence;

    public Task<WdacPolicyEvidence> RecordPolicyAsync(
        TenantScope scope, IReadOnlyList<WdacAllowlistEntry> entries, string issuedBy, string issuedByRole,
        CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<WdacPolicyEvidence?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value), out var evidence) ? evidence : null);
}

/// <summary>Store em memória do <see cref="IncidentResponseDrillRecord"/>.</summary>
internal sealed class InMemoryIncidentResponseDrillStore : IIncidentResponseDrillStore
{
    private readonly Dictionary<(Guid, Guid, IncidentResponseDrillType), IncidentResponseDrillRecord> _latest = [];

    public void Seed(TenantScope scope, IncidentResponseDrillRecord record) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, record.DrillType)] = record;

    public Task<IncidentResponseDrillRecord> RecordDrillAsync(
        TenantScope scope, IncidentResponseDrillType drillType, IncidentResponseDrillOutcome outcome, DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc, Sha256Hash evidenceDigest, string disposition, string executedBy, string executedByRole,
        CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<IncidentResponseDrillRecord?> GetLatestAsync(TenantScope scope, IncidentResponseDrillType drillType, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, drillType), out var record) ? record : null);
}

/// <summary>Store em memória do <see cref="BuildProvenanceRecord"/>.</summary>
internal sealed class InMemoryBuildProvenanceStore : IBuildProvenanceStore
{
    private readonly Dictionary<(Guid, Guid, string), BuildProvenanceRecord> _latest = [];

    public void Seed(TenantScope scope, BuildProvenanceRecord record) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, record.ArtifactName)] = record;

    public Task<BuildProvenanceRecord> ApproveAsync(
        TenantScope scope, string artifactName, string sourceCommitSha, string builderIdentity, DateTimeOffset buildTimestampUtc,
        Sha256Hash artifactDigest, string approvedBy, string approvedByRole, CorrelationId correlation, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<BuildProvenanceRecord?> GetLatestAsync(TenantScope scope, string artifactName, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, artifactName), out var record) ? record : null);
}

/// <summary>Store em memória do <see cref="RecoveryReadinessRecord"/>.</summary>
internal sealed class InMemoryRecoveryReadinessStore : IRecoveryReadinessStore
{
    private readonly Dictionary<(Guid, Guid, RecoveryExerciseType), List<RecoveryReadinessRecord>> _history = [];

    public void Seed(TenantScope scope, RecoveryReadinessRecord record)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, record.ExerciseType);
        if (!_history.TryGetValue(key, out var list))
        {
            list = [];
            _history[key] = list;
        }

        list.Add(record);
    }

    public Task<RecoveryReadinessRecord> RecordExerciseAsync(
        TenantScope scope, RecoveryExerciseType exerciseType, RecoveryReadinessStatus status, RecoveryObjective objective,
        TimeSpan? objectiveThreshold, RecoveryObjectiveMeasurement? measurement, Sha256Hash evidenceFingerprint,
        string failureDomain, string notes, string executedBy, string executedByRole, CorrelationId correlation,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<RecoveryReadinessRecord?> GetLatestAsync(TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, exerciseType);
        return Task.FromResult(_history.TryGetValue(key, out var list) ? list[^1] : null);
    }

    public Task<IReadOnlyList<RecoveryReadinessRecord>> GetHistoryAsync(TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, exerciseType);
        return Task.FromResult<IReadOnlyList<RecoveryReadinessRecord>>(_history.TryGetValue(key, out var list) ? [.. list] : []);
    }
}

/// <summary>Store em memória do <see cref="CapabilityEvidence"/> (única, por rota — mesmo desenho do store SQL real).</summary>
internal sealed class InMemoryCapabilityEvidenceStore : ICapabilityEvidenceStore
{
    private readonly Dictionary<(Guid, Guid, TargetProvider, string), CapabilityEvidence> _latest = [];

    public void Seed(TenantScope scope, CapabilityEvidence evidence) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, evidence.Provider, evidence.Route.Value)] = evidence;

    public Task<CapabilityEvidence?> GetLatestAsync(
        TenantScope scope, TargetProvider provider, PurviewCapabilityRoute route, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, provider, route.Value), out var evidence) ? evidence : null);

    public Task<CapabilityEvidenceAppendResult> AppendAsync(CapabilityEvidence evidence, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");
}

/// <summary>Store em memória do <see cref="MailboxPrecheckSnapshot"/> — <see cref="GetLatestAcrossMailboxesAsync"/> replica a semântica "mais recente por RecordedAtUtc, entre TODOS os mailboxes" do store SQL real.</summary>
internal sealed class InMemoryMailboxPrecheckStore : IMailboxPrecheckStore
{
    private readonly Dictionary<(Guid, Guid), List<MailboxPrecheckSnapshot>> _byScope = [];

    public void Seed(TenantScope scope, MailboxPrecheckSnapshot snapshot)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        if (!_byScope.TryGetValue(key, out var list))
        {
            list = [];
            _byScope[key] = list;
        }

        list.Add(snapshot);
    }

    public Task<MailboxPrecheckSnapshot?> GetLatestAsync(TenantScope scope, TargetArchiveId mailbox, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        var match = _byScope.TryGetValue(key, out var list)
            ? list.Where(snapshot => snapshot.Mailbox.Identity.Equals(mailbox)).MaxBy(snapshot => snapshot.Version)
            : null;
        return Task.FromResult(match);
    }

    public Task<MailboxPrecheckSnapshot?> GetLatestAcrossMailboxesAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        var match = _byScope.TryGetValue(key, out var list) ? list.MaxBy(snapshot => snapshot.RecordedAtUtc) : null;
        return Task.FromResult(match);
    }

    public Task<MailboxPrecheckAppendResult> AppendAsync(MailboxPrecheckSnapshot snapshot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");
}

/// <summary>Store em memória do <see cref="MappingValidationAttempt"/> — <see cref="GetLatestAsync(TenantScope,CancellationToken)"/> replica "mais recente por CreatedAtUtc, no tenant/projeto" do store SQL real.</summary>
internal sealed class InMemoryMappingValidationStore : IMappingValidationStore
{
    private readonly Dictionary<(Guid, Guid), List<MappingValidationAttempt>> _byScope = [];

    public void Seed(MappingValidationAttempt attempt)
    {
        var key = (attempt.Scope.Tenant.Value, attempt.Scope.Project.Value);
        if (!_byScope.TryGetValue(key, out var list))
        {
            list = [];
            _byScope[key] = list;
        }

        list.Add(attempt);
    }

    public Task<MappingValidationPersistResult> PersistAsync(MappingValidationAttempt attempt, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<MappingValidationAttempt?> GetAsync(TenantScope scope, Guid validationId, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        var match = _byScope.TryGetValue(key, out var list) ? list.SingleOrDefault(attempt => attempt.ValidationId == validationId) : null;
        return Task.FromResult(match);
    }

    public Task<MappingValidationAttempt?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        var match = _byScope.TryGetValue(key, out var list) ? list.MaxBy(attempt => attempt.CreatedAtUtc) : null;
        return Task.FromResult(match);
    }
}

/// <summary>Store em memória do <see cref="PurviewUploadAttemptRecord"/> — <see cref="GetLatestAcrossRequestsAsync"/> replica "mais recente por CompletedAtUtc, entre TODOS os pedidos" do store SQL real.</summary>
internal sealed class InMemoryPurviewUploadAttemptStore : IPurviewUploadAttemptStore
{
    private readonly Dictionary<(Guid, Guid), List<PurviewUploadAttemptRecord>> _byScope = [];

    public void Seed(TenantScope scope, PurviewUploadAttemptRecord record)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        if (!_byScope.TryGetValue(key, out var list))
        {
            list = [];
            _byScope[key] = list;
        }

        list.Add(record);
    }

    public Task AppendAsync(TenantScope scope, PurviewUploadAttemptRecord record, JobFence? fence, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");

    public Task<PurviewUploadAttemptRecord?> GetLatestAsync(TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        var match = _byScope.TryGetValue(key, out var list)
            ? list.Where(record => record.Request == request).MaxBy(record => record.AttemptNumber)
            : null;
        return Task.FromResult(match);
    }

    public Task<PurviewUploadAttemptRecord?> GetLatestAcrossRequestsAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        var match = _byScope.TryGetValue(key, out var list) ? list.MaxBy(record => record.CompletedAtUtc) : null;
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<PurviewUploadAttemptRecord>> ListAttemptsAsync(
        TenantScope scope, PurviewUploadRequestId request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de agregação.");
}

/// <summary>Store em memória do <see cref="ReadinessControlAttestation"/>.</summary>
internal sealed class InMemoryReadinessControlAttestationStore : IReadinessControlAttestationStore
{
    private readonly Dictionary<(Guid, Guid, string), ReadinessControlAttestation> _latest = [];

    /// <summary>Injeta uma atestação diretamente (bypassa o use case de submissão) — usado para provar a defesa em profundidade do Compose contra dados legados/adulterados.</summary>
    public void SeedBypassingUseCase(TenantScope scope, ReadinessControlAttestation attestation) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, attestation.ControlId.Value)] = attestation;

    public Task<ReadinessControlAttestation> RecordAttestationAsync(
        TenantScope scope, ReadinessControlId controlId, ReadinessControlStatus status, ReadinessEvidenceReference evidence,
        string reasonCode, string submittedBy, string submittedByRole, CorrelationId correlation, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, controlId.Value);
        var candidate = ReadinessControlAttestation.Create(
            scope.Tenant, scope.Project, controlId, attestationVersion: 1, status, evidence, reasonCode, submittedBy,
            submittedByRole, correlation, now);

        if (_latest.TryGetValue(key, out var current)
            && string.Equals(current.ContentFingerprint.Value, candidate.ContentFingerprint.Value, StringComparison.Ordinal))
        {
            // Replay idêntico: converge sem alocar uma nova versão (mesma semântica da store SQL real).
            return Task.FromResult(current);
        }

        var nextVersion = (current?.AttestationVersion ?? 0) + 1;
        var record = ReadinessControlAttestation.Create(
            scope.Tenant, scope.Project, controlId, nextVersion, status, evidence, reasonCode, submittedBy,
            submittedByRole, correlation, now);
        _latest[key] = record;
        return Task.FromResult(record);
    }

    public Task<ReadinessControlAttestation?> GetLatestAsync(TenantScope scope, ReadinessControlId controlId, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, controlId.Value), out var record) ? record : null);

    public Task<IReadOnlyList<ReadinessControlAttestation>> GetLatestForAllAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReadinessControlAttestation>>(
            [.. _latest.Where(entry => entry.Key.Item1 == scope.Tenant.Value && entry.Key.Item2 == scope.Project.Value).Select(entry => entry.Value)]);
}

/// <summary>Store em memória do <see cref="ProductionReadinessReviewSnapshot"/> — replica a semântica real de convergência/versionamento sem SQL.</summary>
internal sealed class InMemoryProductionReadinessReviewStore : IProductionReadinessReviewStore
{
    private readonly Dictionary<(Guid, Guid), List<ProductionReadinessReviewSnapshot>> _history = [];

    public int RecordCallCount { get; private set; }

    public Task<ProductionReadinessReviewSnapshot> RecordReviewAsync(
        TenantScope scope, string buildCommitSha, Sha256Hash buildArtifactDigest, Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint, IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedControlResults,
        string submittedBy, string submittedByRole, CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RecordCallCount++;
        var key = (scope.Tenant.Value, scope.Project.Value);
        if (!_history.TryGetValue(key, out var list))
        {
            list = [];
            _history[key] = list;
        }

        var candidate = ProductionReadinessReviewSnapshot.Compose(
            scope.Tenant, scope.Project, reviewVersion: 1, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, resolvedControlResults, submittedBy, submittedByRole, correlation, now);

        var current = list.Count > 0 ? list[^1] : null;
        if (current is not null && string.Equals(current.ReviewFingerprint.Value, candidate.ReviewFingerprint.Value, StringComparison.Ordinal))
        {
            return Task.FromResult(current);
        }

        var nextVersion = (current?.ReviewVersion ?? 0) + 1;
        var record = ProductionReadinessReviewSnapshot.Compose(
            scope.Tenant, scope.Project, nextVersion, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, resolvedControlResults, submittedBy, submittedByRole, correlation, now);
        list.Add(record);
        return Task.FromResult(record);
    }

    public Task<ProductionReadinessReviewSnapshot?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult(_history.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null);
    }

    public Task<IReadOnlyList<ProductionReadinessReviewSnapshot>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult<IReadOnlyList<ProductionReadinessReviewSnapshot>>(_history.TryGetValue(key, out var list) ? [.. list] : []);
    }
}
