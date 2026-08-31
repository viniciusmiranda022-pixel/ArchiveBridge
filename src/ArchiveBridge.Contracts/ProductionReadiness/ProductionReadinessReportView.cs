using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.ProductionReadiness;

/// <summary>
/// View SANITIZADA de UM controle para exibição/relatório (AB-I8-001 escopo obrigatório item 8) — projeção
/// direta de <see cref="ReadinessControlResult"/>; nunca carrega mais do que o snapshot já validou como
/// livre de segredo/PII (<c>ReasonCode</c>/<c>Evidence.Locator</c> já passaram pelo guarda de
/// <see cref="ArchiveBridge.Domain.Security.SecretRedactor"/> na escrita).
/// </summary>
public sealed record ProductionReadinessControlView(
    string ControlId,
    ReadinessGateGroup Group,
    string Description,
    ReadinessControlEvidenceSource EvidenceSource,
    ReadinessControlStatus Status,
    string EvidenceLocator,
    string ReasonCode,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Relatório SANITIZADO de UMA revisão de produção (AB-I8-001 escopo obrigatório item 8) — explica
/// precisamente por que o sistema está ou não <see cref="ProductionReadinessOutcome.ReadyForCanary"/>.
/// Projeção pura de <see cref="ProductionReadinessReviewSnapshot"/>; nunca inclui segredo, SAS, token,
/// caminho sensível ou PII desnecessária (STOP-THE-LINE do work order). NUNCA representa aprovação de
/// canário/go-live real — ver <see cref="Disclaimer"/>.
/// </summary>
public sealed record ProductionReadinessReportView
{
    /// <summary>Disclaimer FIXO exposto por todo relatório — nunca certifica canário/go-live/aprovação humana final.</summary>
    public const string Disclaimer =
        "This report reflects only the aggregation of evidence already produced by prior increments (I6/I7) and " +
        "explicit manual attestations recorded through this system. It NEVER authorizes a production canary, " +
        "go-live, or final human sign-off — those remain out of scope for this Passo (runbook §48/§49).";

    /// <summary>Cria o relatório sanitizado.</summary>
    public ProductionReadinessReportView(
        int reviewVersion,
        string buildCommitSha,
        ProductionReadinessOutcome outcome,
        IReadOnlyList<ProductionReadinessControlView> controls,
        IReadOnlyList<string> blockerSummaries,
        DateTimeOffset generatedAtUtc)
    {
        ReviewVersion = reviewVersion;
        BuildCommitSha = buildCommitSha;
        Outcome = outcome;
        Controls = controls;
        BlockerSummaries = blockerSummaries;
        GeneratedAtUtc = generatedAtUtc;
    }

    /// <summary>Versão do snapshot revisado.</summary>
    public int ReviewVersion { get; }

    /// <summary>Commit revisado.</summary>
    public string BuildCommitSha { get; }

    /// <summary>Desfecho agregado.</summary>
    public ProductionReadinessOutcome Outcome { get; }

    /// <summary>Todos os controles do catálogo, na ordem determinística do catálogo.</summary>
    public IReadOnlyList<ProductionReadinessControlView> Controls { get; }

    /// <summary>Resumo textual sanitizado de cada blocker (vazio quando <see cref="Outcome"/> é <see cref="ProductionReadinessOutcome.ReadyForCanary"/>).</summary>
    public IReadOnlyList<string> BlockerSummaries { get; }

    /// <summary>Instante em que o snapshot revisado foi gerado.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }
}
