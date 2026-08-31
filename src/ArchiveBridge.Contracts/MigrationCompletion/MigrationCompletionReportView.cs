using ArchiveBridge.Domain.MigrationCompletion;
using ArchiveBridge.Domain.ProductionReadiness;

namespace ArchiveBridge.Contracts.MigrationCompletion;

/// <summary>Visão sanitizada de UM critério de encerramento (§49) para exibição/relatório (AB-I8-010).</summary>
public sealed record MigrationCompletionCriterionView(
    string CriterionId,
    ReadinessControlStatus Status,
    string EvidenceLocator,
    string ReasonCode,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Relatório SANITIZADO de UMA avaliação de encerramento de migração (AB-I8-010) — explica precisamente por
/// que a migração está <c>Eligible</c> ou <c>Blocked</c> para encerramento. Projeção pura de
/// <see cref="MigrationCompletionAssessment"/>; nunca inclui segredo, SAS, token, caminho sensível ou PII
/// desnecessária. NUNCA representa a migração/projeto/wave <c>Completed</c> — ver <see cref="Disclaimer"/>.
/// </summary>
public sealed record MigrationCompletionReportView
{
    /// <summary>Disclaimer FIXO exposto por todo relatório — nunca certifica encerramento efetivo.</summary>
    public const string Disclaimer =
        "This report reflects only the aggregation of migration-completion criterion evidence already produced " +
        "through this system (runbook §49). Eligible means every documented closure criterion is currently " +
        "satisfied by canonical or attested evidence — it NEVER means the project/wave state has been flipped " +
        "to Completed, and it NEVER authorizes decommission or any destructive/irreversible action.";

    /// <summary>Cria o relatório sanitizado.</summary>
    public MigrationCompletionReportView(
        int assessmentVersion,
        MigrationCompletionOutcome outcome,
        bool isCurrent,
        IReadOnlyList<MigrationCompletionCriterionView> criteria,
        IReadOnlyList<string> blockerSummaries,
        DateTimeOffset generatedAtUtc)
    {
        AssessmentVersion = assessmentVersion;
        Outcome = outcome;
        IsCurrent = isCurrent;
        Criteria = criteria;
        BlockerSummaries = blockerSummaries;
        GeneratedAtUtc = generatedAtUtc;
    }

    /// <summary>Versão da avaliação.</summary>
    public int AssessmentVersion { get; }

    /// <summary>Desfecho agregado desta avaliação.</summary>
    public MigrationCompletionOutcome Outcome { get; }

    /// <summary><see langword="true"/> quando esta ainda é a versão vigente do escopo.</summary>
    public bool IsCurrent { get; }

    /// <summary>Todos os onze critérios do catálogo, na ordem determinística do catálogo.</summary>
    public IReadOnlyList<MigrationCompletionCriterionView> Criteria { get; }

    /// <summary>Resumo textual sanitizado de cada blocker (vazio quando <see cref="Outcome"/> é <see cref="MigrationCompletionOutcome.Eligible"/>).</summary>
    public IReadOnlyList<string> BlockerSummaries { get; }

    /// <summary>Instante em que este relatório foi gerado.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }
}
