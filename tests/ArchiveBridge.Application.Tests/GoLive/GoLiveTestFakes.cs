using ArchiveBridge.Contracts.GoLive;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Application.Tests.GoLive;

/// <summary>Store em memória da <see cref="GoLiveAuthorizationDecision"/> — replica a semântica real de convergência/versionamento sem SQL (mesmo padrão das demais InMemory*Store deste repositório).</summary>
internal sealed class InMemoryGoLiveAuthorizationStore : IGoLiveAuthorizationStore
{
    private readonly Dictionary<(Guid Tenant, Guid Project), List<GoLiveAuthorizationDecision>> _byScope = [];

    public int RecordCallCount { get; private set; }

    public Task<GoLiveAuthorizationDecision> AuthorizeAsync(
        TenantScope scope,
        CanaryPlanId canaryPlanId,
        int canaryPlanVersion,
        Sha256Hash canaryPlanFingerprint,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        CanaryOutcome canaryOutcomeAtAuthorization,
        int? currentReadinessReviewVersionAtAuthorization,
        Sha256Hash? currentReadinessReviewFingerprintAtAuthorization,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> operationalResolvedResults,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RecordCallCount++;

        var key = (scope.Tenant.Value, scope.Project.Value);
        if (!_byScope.TryGetValue(key, out var list))
        {
            list = [];
            _byScope[key] = list;
        }

        var current = list.Count > 0 ? list[^1] : null;
        var candidate = GoLiveAuthorizationDecision.Compose(
            scope.Tenant, scope.Project, GoLiveAuthorizationId.New(), authorizationVersion: 1, canaryPlanId, canaryPlanVersion,
            canaryPlanFingerprint, readinessReviewVersion, readinessReviewFingerprint, buildCommitSha, buildArtifactDigest,
            policyVersionFingerprint, capabilityMatrixFingerprint, canaryOutcomeAtAuthorization,
            currentReadinessReviewVersionAtAuthorization, currentReadinessReviewFingerprintAtAuthorization,
            operationalResolvedResults, authorizedBy, authorizedByRole, correlation, now);

        if (current is not null
            && string.Equals(current.AuthorizationFingerprint.Value, candidate.AuthorizationFingerprint.Value, StringComparison.Ordinal))
        {
            return Task.FromResult(current);
        }

        var authorizationId = current?.AuthorizationId ?? GoLiveAuthorizationId.New();
        var nextVersion = (current?.AuthorizationVersion ?? 0) + 1;
        var record = GoLiveAuthorizationDecision.Compose(
            scope.Tenant, scope.Project, authorizationId, nextVersion, canaryPlanId, canaryPlanVersion, canaryPlanFingerprint,
            readinessReviewVersion, readinessReviewFingerprint, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, canaryOutcomeAtAuthorization, currentReadinessReviewVersionAtAuthorization,
            currentReadinessReviewFingerprintAtAuthorization, operationalResolvedResults, authorizedBy, authorizedByRole,
            correlation, now);
        list.Add(record);
        return Task.FromResult(record);
    }

    public Task<GoLiveAuthorizationDecision?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult(_byScope.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null);
    }

    public Task<GoLiveAuthorizationDecision?> GetByVersionAsync(TenantScope scope, int authorizationVersion, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        return Task.FromResult(_byScope.TryGetValue(key, out var list) ? list.FirstOrDefault(r => r.AuthorizationVersion == authorizationVersion) : null);
    }

    public Task<IReadOnlyList<GoLiveAuthorizationDecision>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var key = (scope.Tenant.Value, scope.Project.Value);
        IReadOnlyList<GoLiveAuthorizationDecision> result = _byScope.TryGetValue(key, out var list) ? [.. list] : [];
        return Task.FromResult(result);
    }
}
