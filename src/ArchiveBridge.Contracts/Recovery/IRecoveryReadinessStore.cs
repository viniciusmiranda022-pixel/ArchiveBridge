using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Recovery;

namespace ArchiveBridge.Contracts.Recovery;

/// <summary>
/// Porta de persistência do <see cref="RecoveryReadinessRecord"/> (AB-I7-005). Append-only: uma versão nova
/// NUNCA sobrescreve/edita uma anterior. Toda a decisão de negócio (<see cref="RecoveryReadinessStatus"/>,
/// se o objetivo foi atingido) já foi computada pelo chamador (via <see cref="RecoveryReadinessRecord.Pass"/>/
/// <see cref="RecoveryReadinessRecord.Blocked"/>/<see cref="RecoveryReadinessRecord.NotMeasured"/>) ANTES de
/// <see cref="RecordExerciseAsync"/> — a store nunca reinterpreta essas regras; resolve exclusivamente
/// concorrência/convergência sob lock e persiste.
/// </summary>
public interface IRecoveryReadinessStore
{
    /// <summary>
    /// Aloca a próxima <see cref="RecoveryReadinessRecord.ExerciseVersion"/> deste escopo (tenant/project/tipo
    /// de exercício) sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="RecoveryReadinessRecord.ExerciseFingerprint"/> (replay idêntico; execuções concorrentes
    /// idênticas convergem para uma única versão canônica, nunca duplicam o registro).
    /// </summary>
    /// <exception cref="RecoveryReadinessObjectiveNotMetException">
    /// <paramref name="status"/> é <see cref="RecoveryReadinessStatus.Pass"/> sem medição, ou a medição excede
    /// o alvo objetivo, ou o tipo de exercício é <see cref="RecoveryExerciseType.HaFailover"/> (nunca Pass).
    /// </exception>
    Task<RecoveryReadinessRecord> RecordExerciseAsync(
        TenantScope scope,
        RecoveryExerciseType exerciseType,
        RecoveryReadinessStatus status,
        RecoveryObjective objective,
        TimeSpan? objectiveThreshold,
        RecoveryObjectiveMeasurement? measurement,
        Sha256Hash evidenceFingerprint,
        string failureDomain,
        string notes,
        string executedBy,
        string executedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// O registro VIGENTE (maior <see cref="RecoveryReadinessRecord.ExerciseVersion"/>) deste escopo/tipo —
    /// <see langword="null"/> se nenhum exercício ainda registrado. Revalida
    /// <see cref="RecoveryReadinessRecord.RecordHash"/> contra os campos REALMENTE carregados (fail-closed).
    /// </summary>
    /// <exception cref="RecoveryReadinessIntegrityViolationException">O record_hash persistido diverge do recomputado.</exception>
    Task<RecoveryReadinessRecord?> GetLatestAsync(TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) deste escopo/tipo, em ordem crescente de versão.</summary>
    Task<IReadOnlyList<RecoveryReadinessRecord>> GetHistoryAsync(TenantScope scope, RecoveryExerciseType exerciseType, CancellationToken cancellationToken);
}
