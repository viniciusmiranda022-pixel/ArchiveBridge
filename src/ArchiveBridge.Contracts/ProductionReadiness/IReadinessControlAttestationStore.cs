using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.ProductionReadiness;

/// <summary>
/// Porta de persistência do <see cref="ReadinessControlAttestation"/> (AB-I8-001). Append-only, versionado
/// por (tenant, project, controle). Toda a decisão de negócio (status atestado, permissão de atestar este
/// controle específico) já foi computada pelo chamador via <see cref="ReadinessControlAttestation.Create"/>
/// ANTES de <see cref="RecordAttestationAsync"/> — a store nunca reinterpreta essas regras; resolve
/// exclusivamente concorrência/convergência sob lock e persiste.
/// </summary>
public interface IReadinessControlAttestationStore
{
    /// <summary>
    /// Aloca a próxima <see cref="ReadinessControlAttestation.AttestationVersion"/> deste escopo (tenant/
    /// project/controle) sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="ReadinessControlAttestation.ContentFingerprint"/> (replay idêntico).
    /// </summary>
    /// <exception cref="ProductionReadinessAttestationNotAllowedException"><paramref name="controlId"/> é SystemDerived ou desconhecido.</exception>
    Task<ReadinessControlAttestation> RecordAttestationAsync(
        TenantScope scope,
        ReadinessControlId controlId,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>A atestação VIGENTE (maior versão) deste escopo/controle — <see langword="null"/> se nunca atestado. Revalida integridade fail-closed.</summary>
    Task<ReadinessControlAttestation?> GetLatestAsync(TenantScope scope, ReadinessControlId controlId, CancellationToken cancellationToken);

    /// <summary>A atestação VIGENTE de TODOS os controles já atestados deste escopo — ausente equivale a nunca atestado.</summary>
    Task<IReadOnlyList<ReadinessControlAttestation>> GetLatestForAllAsync(TenantScope scope, CancellationToken cancellationToken);
}
