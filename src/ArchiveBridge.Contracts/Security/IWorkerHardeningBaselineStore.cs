using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;

namespace ArchiveBridge.Contracts.Security;

/// <summary>
/// Porta de persistência do <see cref="WorkerHardeningControlRecord"/> (AB-I7-008). Append-only: uma
/// versão nova NUNCA sobrescreve/edita uma anterior. Toda a decisão de negócio (<see cref="WorkerHardeningStatus"/>)
/// já foi computada pelo chamador via <see cref="WorkerHardeningControlRecord.Pass"/>/<see cref="WorkerHardeningControlRecord.Blocked"/>/
/// <see cref="WorkerHardeningControlRecord.NotMeasured"/> ANTES de <see cref="RecordControlAsync"/> — a store
/// nunca reinterpreta essas regras; resolve exclusivamente concorrência/convergência sob lock e persiste.
/// </summary>
public interface IWorkerHardeningBaselineStore
{
    /// <summary>
    /// Aloca a próxima <see cref="WorkerHardeningControlRecord.ControlVersion"/> deste escopo (tenant/project/controle)
    /// sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="WorkerHardeningControlRecord.ContentFingerprint"/>.
    /// </summary>
    Task<WorkerHardeningControlRecord> RecordControlAsync(
        TenantScope scope,
        WorkerHardeningControl control,
        WorkerHardeningStatus status,
        WorkerHardeningMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string blockedReason,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>O registro VIGENTE (maior versão) deste escopo/controle — <see langword="null"/> se nunca verificado. Revalida integridade fail-closed.</summary>
    Task<WorkerHardeningControlRecord?> GetLatestAsync(TenantScope scope, WorkerHardeningControl control, CancellationToken cancellationToken);

    /// <summary>O registro VIGENTE de TODOS os controles da baseline deste escopo — ausente equivale a nunca verificado.</summary>
    Task<IReadOnlyList<WorkerHardeningControlRecord>> GetLatestForAllControlsAsync(TenantScope scope, CancellationToken cancellationToken);
}
