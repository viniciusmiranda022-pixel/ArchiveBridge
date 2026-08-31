using ArchiveBridge.Domain.Canary;

namespace ArchiveBridge.Contracts.Canary;

/// <summary>
/// View SANITIZADA de UM cenário para exibição/relatório (AB-I8-004) — projeção direta de
/// <see cref="CanaryScenarioResult"/>; nunca carrega mais do que o resultado já validou como livre de
/// segredo/PII (<c>ReasonCode</c>/<c>Evidence.Locator</c> já passaram pelo guarda de
/// <see cref="ArchiveBridge.Domain.Security.SecretRedactor"/> na escrita).
/// </summary>
public sealed record CanaryScenarioView(
    string ScenarioId,
    string Description,
    CanaryScenarioEvidenceSource EvidenceSource,
    CanaryScenarioStatus Status,
    string EvidenceLocator,
    string ReasonCode,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Relatório SANITIZADO de UM plano de canário (AB-I8-004) — explica precisamente por que o canário está ou
/// não <see cref="CanaryOutcome.CanaryPassed"/>, e se o build sob canário ainda é o candidato promovível
/// (<see cref="IsPromotable"/>: <see cref="CanaryOutcome.CanaryPassed"/> E nenhum drift detectado desde a
/// autorização E esta é a versão vigente do plano). Projeção pura de <see cref="CanaryPlan"/> +
/// <see cref="CanaryEvaluation"/>; nunca inclui segredo, SAS, token, caminho sensível ou PII desnecessária
/// (STOP-THE-LINE do work order). NUNCA representa <c>ProductionReady</c>/<c>GoLive</c>/projeto
/// <c>COMPLETED</c> — ver <see cref="Disclaimer"/>.
/// </summary>
public sealed record CanaryPlanReportView
{
    /// <summary>Disclaimer FIXO exposto por todo relatório — nunca certifica go-live/COMPLETED.</summary>
    public const string Disclaimer =
        "This report reflects only the aggregation of canary scenario evidence already produced through this " +
        "system. It NEVER authorizes ProductionReady, GoLive, or a project/wave COMPLETED state — those remain " +
        "out of scope for this Passo (runbook §48/§49). IsPromotable being true only means the SAME build/digest " +
        "already reviewed and canaried remains the current candidate — it is not, by itself, a go-live decision.";

    /// <summary>Cria o relatório sanitizado.</summary>
    public CanaryPlanReportView(
        int planVersion,
        string buildCommitSha,
        CanaryOutcome outcome,
        bool isPromotable,
        bool readinessHasDrifted,
        IReadOnlyList<CanaryScenarioView> scenarios,
        IReadOnlyList<string> blockerSummaries,
        DateTimeOffset generatedAtUtc)
    {
        PlanVersion = planVersion;
        BuildCommitSha = buildCommitSha;
        Outcome = outcome;
        IsPromotable = isPromotable;
        ReadinessHasDrifted = readinessHasDrifted;
        Scenarios = scenarios;
        BlockerSummaries = blockerSummaries;
        GeneratedAtUtc = generatedAtUtc;
    }

    /// <summary>Versão do plano avaliado.</summary>
    public int PlanVersion { get; }

    /// <summary>Commit sob canário.</summary>
    public string BuildCommitSha { get; }

    /// <summary>Desfecho agregado deste plano.</summary>
    public CanaryOutcome Outcome { get; }

    /// <summary>
    /// <see langword="true"/> somente quando <see cref="Outcome"/> é <see cref="CanaryOutcome.CanaryPassed"/>,
    /// esta é a versão vigente do plano deste escopo, E o Production Readiness Review canônico atual ainda
    /// corresponde exatamente ao vinculado por este plano (nenhum drift desde a autorização — escopo
    /// obrigatório item 5).
    /// </summary>
    public bool IsPromotable { get; }

    /// <summary>
    /// <see langword="true"/> quando o Production Readiness Review canônico vigente do (tenant, project) já
    /// não corresponde ao vinculado por este plano (nova revisão, build/digest/policy/capability diferentes)
    /// — mesmo com <see cref="Outcome"/> <see cref="CanaryOutcome.CanaryPassed"/>, a promoção exige um novo
    /// canário quando este campo é <see langword="true"/>.
    /// </summary>
    public bool ReadinessHasDrifted { get; }

    /// <summary>Todos os cenários do catálogo, na ordem determinística do catálogo.</summary>
    public IReadOnlyList<CanaryScenarioView> Scenarios { get; }

    /// <summary>Resumo textual sanitizado de cada blocker (vazio quando <see cref="Outcome"/> é <see cref="CanaryOutcome.CanaryPassed"/>).</summary>
    public IReadOnlyList<string> BlockerSummaries { get; }

    /// <summary>Instante em que este relatório foi gerado.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }
}
