using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.GoLive;

/// <summary>
/// Porta de persistência do <see cref="GoLiveAuthorizationDecision"/> (AB-I8-010). Append-only, versionado por
/// (tenant, project). Toda a decisão de negócio (gate de entrada, agregação, invariante de mesmo build) já foi
/// computada pelo chamador via <see cref="GoLiveAuthorizationDecision.Compose"/> ANTES de
/// <see cref="AuthorizeAsync"/> — a store nunca reinterpreta essas regras; resolve exclusivamente
/// concorrência/convergência sob lock e persiste.
/// </summary>
public interface IGoLiveAuthorizationStore
{
    /// <summary>
    /// Aloca a próxima <see cref="GoLiveAuthorizationDecision.AuthorizationVersion"/> deste escopo (tenant/
    /// project) sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="GoLiveAuthorizationDecision.AuthorizationFingerprint"/> (replay idêntico; decisões
    /// concorrentes idênticas convergem para uma única versão canônica, nunca duas autorizações canônicas
    /// conflitantes). Reaproveita o <see cref="GoLiveAuthorizationId"/> já existente deste escopo (se houver)
    /// em toda versão nova.
    /// </summary>
    Task<GoLiveAuthorizationDecision> AuthorizeAsync(
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
        CancellationToken cancellationToken);

    /// <summary>A decisão VIGENTE (maior versão) deste escopo — <see langword="null"/> se nenhuma ainda registrada. Revalida integridade fail-closed.</summary>
    Task<GoLiveAuthorizationDecision?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Uma versão específica da decisão — <see langword="null"/> se inexistente/fora do escopo (anti-IDOR).</summary>
    Task<GoLiveAuthorizationDecision?> GetByVersionAsync(TenantScope scope, int authorizationVersion, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) deste escopo, em ordem crescente de versão.</summary>
    Task<IReadOnlyList<GoLiveAuthorizationDecision>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken);
}
