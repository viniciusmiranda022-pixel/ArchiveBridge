using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.ProductionReadiness;

/// <summary>
/// Porta de persistência do <see cref="ProductionReadinessReviewSnapshot"/> (AB-I8-001). Append-only,
/// versionado por (tenant, project). Toda a decisão de negócio (agregação dos controles, outcome) já foi
/// computada pelo chamador via <see cref="ProductionReadinessReviewSnapshot.Compose"/> ANTES de
/// <see cref="RecordReviewAsync"/> — a store nunca reinterpreta essas regras; resolve exclusivamente
/// concorrência/convergência sob lock e persiste.
/// </summary>
public interface IProductionReadinessReviewStore
{
    /// <summary>
    /// Aloca a próxima <see cref="ProductionReadinessReviewSnapshot.ReviewVersion"/> deste escopo (tenant/
    /// project) sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="ProductionReadinessReviewSnapshot.ReviewFingerprint"/> (replay idêntico; composições
    /// concorrentes idênticas convergem para uma única versão canônica, nunca duplicam o snapshot).
    /// </summary>
    Task<ProductionReadinessReviewSnapshot> RecordReviewAsync(
        TenantScope scope,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedControlResults,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// O snapshot VIGENTE (maior versão) deste escopo — <see langword="null"/> se nenhuma revisão ainda
    /// composta. Revalida integridade fail-closed. NOTA: este é o ÚLTIMO snapshot COMPOSTO, não
    /// necessariamente o estado ATUAL da evidência — um chamador que precise do estado mais recente deve
    /// chamar <see cref="RecordReviewAsync"/> novamente (que converge idempotentemente se nada mudou).
    /// </summary>
    Task<ProductionReadinessReviewSnapshot?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) deste escopo, em ordem crescente de versão.</summary>
    Task<IReadOnlyList<ProductionReadinessReviewSnapshot>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken);
}
