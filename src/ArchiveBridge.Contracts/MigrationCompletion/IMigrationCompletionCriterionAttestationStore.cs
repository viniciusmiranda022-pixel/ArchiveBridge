using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.MigrationCompletion;

/// <summary>
/// Porta de persistência do <see cref="MigrationCompletionCriterionAttestation"/> (AB-I8-010). Append-only,
/// versionado por (tenant, project, critério). Toda a decisão de negócio (status atestado, permissão de
/// atestar este critério específico) já foi computada pelo chamador via
/// <see cref="MigrationCompletionCriterionAttestation.Create"/> ANTES de <see cref="RecordAttestationAsync"/> —
/// a store nunca reinterpreta essas regras; resolve exclusivamente concorrência/convergência sob lock e
/// persiste.
/// </summary>
public interface IMigrationCompletionCriterionAttestationStore
{
    /// <summary>
    /// Aloca a próxima <see cref="MigrationCompletionCriterionAttestation.AttestationVersion"/> deste escopo
    /// (tenant/project/critério) sob lock — ou converge idempotentemente para uma versão já persistida com o
    /// MESMO <see cref="MigrationCompletionCriterionAttestation.ContentFingerprint"/> (replay idêntico).
    /// </summary>
    /// <exception cref="MigrationCompletionAttestationNotAllowedException"><paramref name="criterionId"/> é SystemDerived ou desconhecido.</exception>
    Task<MigrationCompletionCriterionAttestation> RecordAttestationAsync(
        TenantScope scope,
        MigrationCompletionCriterionId criterionId,
        ReadinessControlStatus status,
        ReadinessEvidenceReference evidence,
        string reasonCode,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>A atestação VIGENTE (maior versão) deste escopo/critério — <see langword="null"/> se nunca atestado. Revalida integridade fail-closed.</summary>
    Task<MigrationCompletionCriterionAttestation?> GetLatestAsync(TenantScope scope, MigrationCompletionCriterionId criterionId, CancellationToken cancellationToken);

    /// <summary>A atestação VIGENTE de TODOS os critérios já atestados deste escopo — ausente equivale a nunca atestado.</summary>
    Task<IReadOnlyList<MigrationCompletionCriterionAttestation>> GetLatestForAllAsync(TenantScope scope, CancellationToken cancellationToken);
}
