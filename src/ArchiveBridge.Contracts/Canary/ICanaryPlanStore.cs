using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.Canary;

/// <summary>
/// Porta de persistência do <see cref="CanaryPlan"/> (AB-I8-004). Append-only, versionado por (tenant,
/// project). Toda a decisão de negócio (gate de entrada, invariante de mesmo build) já foi computada pelo
/// chamador via <see cref="CanaryPlan.Compose"/> ANTES de <see cref="AuthorizeAsync"/> — a store nunca
/// reinterpreta essas regras; resolve exclusivamente concorrência/convergência sob lock e persiste.
/// </summary>
public interface ICanaryPlanStore
{
    /// <summary>
    /// Aloca a próxima <see cref="CanaryPlan.PlanVersion"/> deste escopo (tenant/project) sob lock — ou
    /// converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="CanaryPlan.PlanFingerprint"/> (replay idêntico; autorizações concorrentes idênticas
    /// convergem para uma única versão canônica, nunca duplicam o plano). Reaproveita o
    /// <see cref="CanaryPlan.PlanId"/> já existente deste escopo (se houver) em toda versão nova — a
    /// identidade do plano é estável, apenas a versão avança.
    /// </summary>
    Task<CanaryPlan> AuthorizeAsync(
        TenantScope scope,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        ProductionReadinessOutcome readinessOutcome,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// O plano VIGENTE (maior versão) deste escopo — <see langword="null"/> se nenhum ainda autorizado.
    /// Revalida integridade fail-closed.
    /// </summary>
    Task<CanaryPlan?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Uma versão específica do plano — <see langword="null"/> se inexistente/fora do escopo (anti-IDOR).</summary>
    Task<CanaryPlan?> GetByVersionAsync(TenantScope scope, int planVersion, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) deste escopo, em ordem crescente de versão.</summary>
    Task<IReadOnlyList<CanaryPlan>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken);
}
