using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.MigrationCompletion;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Tests.MigrationCompletion;

/// <summary>Store em memória do <see cref="PurviewServiceResultReportEvidence"/> — só o necessário para COMPLETION.PROVIDER_RESULTS_COLLECTED (AB-I8-010).</summary>
internal sealed class InMemoryPurviewServiceResultReportStore : IPurviewServiceResultReportStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project, Guid Wave, string JobName), PurviewServiceResultReportEvidence> _latest = [];

    public void Seed(TenantScope scope, WaveId wave, PurviewImportJobName jobName, PurviewServiceResultReportEvidence evidence) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, wave.Value, jobName.Value)] = evidence;

    public Task<PurviewServiceResultReportEvidence?> GetByContentHashAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, Sha256Hash contentSha256, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de encerramento de migração.");

    public Task<PurviewServiceResultReportEvidence> PersistAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, ReadOnlyMemory<byte> rawBytes,
        IReadOnlyList<PurviewServiceResultRow> rows, int? declaredTotalRows, string uploadedBy, DateTimeOffset now, JobFence? fence,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de encerramento de migração.");

    public Task<PurviewServiceResultReportEvidence?> GetLatestAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, wave.Value, plannedJobName.Value), out var evidence) ? evidence : null);

    public Task<IReadOnlyList<PurviewServiceResultRow>> GetRowsAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, int reportVersion, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Não usado pelos testes de encerramento de migração.");
}

/// <summary>Store em memória da <see cref="MigrationCompletionCriterionAttestation"/> — mesmo padrão de InMemoryReadinessControlAttestationStore (Passo 1).</summary>
internal sealed class InMemoryMigrationCompletionCriterionAttestationStore : IMigrationCompletionCriterionAttestationStore
{
    private readonly Dictionary<(Guid, Guid, string), MigrationCompletionCriterionAttestation> _latest = [];

    public void SeedBypassingUseCase(TenantScope scope, MigrationCompletionCriterionAttestation attestation) =>
        _latest[(scope.Tenant.Value, scope.Project.Value, attestation.CriterionId.Value)] = attestation;

    public Task<MigrationCompletionCriterionAttestation> RecordAttestationAsync(
        TenantScope scope, MigrationCompletionCriterionId criterionId, ReadinessControlStatus status, ReadinessEvidenceReference evidence,
        string reasonCode, string submittedBy, string submittedByRole, CorrelationId correlation, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value, criterionId.Value);
        var candidate = MigrationCompletionCriterionAttestation.Create(
            scope.Tenant, scope.Project, criterionId, attestationVersion: 1, status, evidence, reasonCode, submittedBy, submittedByRole,
            correlation, now);

        if (_latest.TryGetValue(key, out var current)
            && string.Equals(current.ContentFingerprint.Value, candidate.ContentFingerprint.Value, StringComparison.Ordinal))
        {
            return Task.FromResult(current);
        }

        var nextVersion = (current?.AttestationVersion ?? 0) + 1;
        var record = MigrationCompletionCriterionAttestation.Create(
            scope.Tenant, scope.Project, criterionId, nextVersion, status, evidence, reasonCode, submittedBy, submittedByRole,
            correlation, now);
        _latest[key] = record;
        return Task.FromResult(record);
    }

    public Task<MigrationCompletionCriterionAttestation?> GetLatestAsync(
        TenantScope scope, MigrationCompletionCriterionId criterionId, CancellationToken cancellationToken) =>
        Task.FromResult(_latest.TryGetValue((scope.Tenant.Value, scope.Project.Value, criterionId.Value), out var record) ? record : null);

    public Task<IReadOnlyList<MigrationCompletionCriterionAttestation>> GetLatestForAllAsync(TenantScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MigrationCompletionCriterionAttestation>>(
            [.. _latest.Where(entry => entry.Key.Item1 == scope.Tenant.Value && entry.Key.Item2 == scope.Project.Value).Select(entry => entry.Value)]);
}

/// <summary>Store em memória da <see cref="MigrationCompletionAssessment"/> — replica a semântica real de convergência/versionamento sem SQL.</summary>
internal sealed class InMemoryMigrationCompletionAssessmentStore : IMigrationCompletionAssessmentStore
{
    private readonly Dictionary<(Guid, Guid), List<MigrationCompletionAssessment>> _history = [];

    public int RecordCallCount { get; private set; }

    public Task<MigrationCompletionAssessment> RecordAssessmentAsync(
        TenantScope scope, WaveId anchorWave, PurviewImportJobName anchorPlannedJobName,
        IReadOnlyDictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> resolvedCriterionResults,
        string submittedBy, string submittedByRole, CorrelationId correlation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RecordCallCount++;
        var key = (scope.Tenant.Value, scope.Project.Value);
        if (!_history.TryGetValue(key, out var list))
        {
            list = [];
            _history[key] = list;
        }

        var candidate = MigrationCompletionAssessment.Compose(
            scope.Tenant, scope.Project, 1, anchorWave, anchorPlannedJobName, resolvedCriterionResults, submittedBy, submittedByRole,
            correlation, now);

        var current = list.Count > 0 ? list[^1] : null;
        if (current is not null && string.Equals(current.AssessmentFingerprint.Value, candidate.AssessmentFingerprint.Value, StringComparison.Ordinal))
        {
            return Task.FromResult(current);
        }

        var nextVersion = (current?.AssessmentVersion ?? 0) + 1;
        var record = MigrationCompletionAssessment.Compose(
            scope.Tenant, scope.Project, nextVersion, anchorWave, anchorPlannedJobName, resolvedCriterionResults, submittedBy,
            submittedByRole, correlation, now);
        list.Add(record);
        return Task.FromResult(record);
    }

    public Task<MigrationCompletionAssessment?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult(_history.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null);
    }

    public Task<IReadOnlyList<MigrationCompletionAssessment>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        IReadOnlyList<MigrationCompletionAssessment> result = _history.TryGetValue(key, out var list) ? [.. list] : [];
        return Task.FromResult(result);
    }
}
