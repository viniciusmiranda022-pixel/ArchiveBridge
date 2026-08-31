using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.GoLive;

/// <summary>Visão sanitizada de UM controle operacional/M365 revalidado fresco (AB-I8-010, escopo obrigatório item 12).</summary>
public sealed record GoLiveOperationalControlView(
    string ControlId,
    ReadinessGateGroup Group,
    ReadinessControlStatus Status,
    string EvidenceLocator,
    string ReasonCode,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Relatório SANITIZADO de UMA decisão de go-live (AB-I8-010) — explica precisamente por que a promoção está
/// <c>GoLiveAuthorized</c> ou <c>Blocked</c>, e se a decisão vigente ainda é vigente (nenhuma supersession por
/// drift desde a autorização). Projeção pura de <see cref="GoLiveAuthorizationDecision"/>; nunca inclui
/// segredo, SAS, token, caminho sensível ou PII desnecessária. NUNCA representa migração/projeto/wave
/// <c>Completed</c> — ver <see cref="Disclaimer"/>.
/// </summary>
public sealed record GoLiveReportView
{
    /// <summary>Disclaimer FIXO exposto por todo relatório — nunca certifica encerramento de migração.</summary>
    public const string Disclaimer =
        "This report reflects only the aggregation of canary/readiness/operational evidence already produced " +
        "through this system. GoLiveAuthorized means the first real low-criticality wave (runbook §48 item 185) " +
        "is authorized to proceed — it NEVER means the migration/project/wave is Completed. Migration completion " +
        "criteria (runbook §49) are evaluated independently and are never satisfied implicitly by this outcome.";

    /// <summary>Cria o relatório sanitizado.</summary>
    public GoLiveReportView(
        int authorizationVersion,
        string buildCommitSha,
        GoLiveOutcome outcome,
        bool isCurrent,
        IReadOnlyList<GoLiveOperationalControlView> operationalControls,
        IReadOnlyList<string> blockerSummaries,
        DateTimeOffset generatedAtUtc)
    {
        AuthorizationVersion = authorizationVersion;
        BuildCommitSha = buildCommitSha;
        Outcome = outcome;
        IsCurrent = isCurrent;
        OperationalControls = operationalControls;
        BlockerSummaries = blockerSummaries;
        GeneratedAtUtc = generatedAtUtc;
    }

    /// <summary>Versão da decisão avaliada.</summary>
    public int AuthorizationVersion { get; }

    /// <summary>Build/commit promovido — sempre exatamente o mesmo já revisado e canariado.</summary>
    public string BuildCommitSha { get; }

    /// <summary>Desfecho agregado desta decisão.</summary>
    public GoLiveOutcome Outcome { get; }

    /// <summary><see langword="true"/> quando esta ainda é a versão vigente do escopo (nenhuma versão mais recente já registrada).</summary>
    public bool IsCurrent { get; }

    /// <summary>Todos os controles operacionais/M365 revalidados, na ordem determinística do catálogo.</summary>
    public IReadOnlyList<GoLiveOperationalControlView> OperationalControls { get; }

    /// <summary>Resumo textual sanitizado de cada blocker (vazio quando <see cref="Outcome"/> é <see cref="GoLiveOutcome.GoLiveAuthorized"/>).</summary>
    public IReadOnlyList<string> BlockerSummaries { get; }

    /// <summary>Instante em que este relatório foi gerado.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }
}
