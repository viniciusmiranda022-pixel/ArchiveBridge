using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.MigrationCompletion;

/// <summary>
/// Porta de persistência do <see cref="MigrationCompletionAssessment"/> (AB-I8-010). Append-only, versionado
/// por (tenant, project). Toda a decisão de negócio (agregação dos critérios, outcome) já foi computada pelo
/// chamador via <see cref="MigrationCompletionAssessment.Compose"/> ANTES de <see cref="RecordAssessmentAsync"/>
/// — a store nunca reinterpreta essas regras; resolve exclusivamente concorrência/convergência sob lock e
/// persiste.
/// </summary>
public interface IMigrationCompletionAssessmentStore
{
    /// <summary>
    /// Aloca a próxima <see cref="MigrationCompletionAssessment.AssessmentVersion"/> deste escopo (tenant/
    /// project) sob lock — ou converge idempotentemente para uma versão já persistida com o MESMO
    /// <see cref="MigrationCompletionAssessment.AssessmentFingerprint"/> (replay idêntico; composições
    /// concorrentes idênticas convergem para uma única versão canônica).
    /// </summary>
    Task<MigrationCompletionAssessment> RecordAssessmentAsync(
        TenantScope scope,
        WaveId anchorWave,
        PurviewImportJobName anchorPlannedJobName,
        IReadOnlyDictionary<MigrationCompletionCriterionId, MigrationCompletionCriterionResult> resolvedCriterionResults,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// A avaliação VIGENTE (maior versão) deste escopo — <see langword="null"/> se nenhuma ainda composta.
    /// Revalida integridade fail-closed. NOTA: esta é a ÚLTIMA avaliação COMPOSTA, não necessariamente o
    /// estado ATUAL da evidência.
    /// </summary>
    Task<MigrationCompletionAssessment?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) deste escopo, em ordem crescente de versão.</summary>
    Task<IReadOnlyList<MigrationCompletionAssessment>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken);
}
