using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Contracts.Canary;

/// <summary>
/// Porta de persistência dos <see cref="CanaryScenarioResult"/> submetidos ao longo do tempo (AB-I8-004,
/// escopo obrigatório item 6: "evidência deve ser append-only/tamper-evident e replay idêntico deve
/// convergir"), sempre escopados a UMA versão específica e VIGENTE de <see cref="CanaryPlan"/> — nunca a
/// "o plano mais recente" implicitamente, para que drift do plano (escopo obrigatório item 5) invalide
/// submissões futuras contra uma versão superada. Toda a decisão de negócio (se o resultado é válido para o
/// cenário) já foi computada pelo chamador ANTES de <see cref="RecordResultAsync"/> — a store nunca
/// reinterpreta essas regras; resolve exclusivamente concorrência/convergência sob lock e persiste.
/// </summary>
public interface ICanaryScenarioResultStore
{
    /// <summary>
    /// Aloca a próxima versão de resultado deste escopo/plano/cenário sob lock — ou converge idempotentemente
    /// para um resultado já persistido com o MESMO conteúdo (replay idêntico; submissões concorrentes
    /// idênticas convergem para um único resultado canônico, nunca duplicam a linha).
    /// </summary>
    /// <exception cref="CanaryPlanSupersededException"><paramref name="planVersion"/> não é mais a versão vigente do plano deste escopo.</exception>
    Task<CanaryScenarioResult> RecordResultAsync(
        TenantScope scope,
        int planVersion,
        CanaryScenarioId scenarioId,
        CanaryScenarioStatus status,
        CanaryEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>O resultado VIGENTE (mais recente) de UM cenário desta versão do plano — <see langword="null"/> se nunca submetido.</summary>
    Task<CanaryScenarioResult?> GetLatestAsync(
        TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CancellationToken cancellationToken);

    /// <summary>O resultado VIGENTE de CADA cenário já submetido para esta versão do plano (cenários nunca submetidos ficam ausentes — nunca fabricados).</summary>
    Task<IReadOnlyDictionary<CanaryScenarioId, CanaryScenarioResult>> GetAllLatestForPlanAsync(
        TenantScope scope, int planVersion, CancellationToken cancellationToken);

    /// <summary>TODAS as versões (histórico completo, append-only) de UM cenário desta versão do plano, em ordem crescente.</summary>
    Task<IReadOnlyList<CanaryScenarioResult>> GetHistoryAsync(
        TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CancellationToken cancellationToken);
}
