using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Resultado da avaliação: o desfecho de completude da evidência do provider (nunca um resultado de
/// reconciliação final — ver <see cref="PurviewServiceResultCompletenessOutcome"/>) e as contagens que o
/// sustentam, para auditoria/exibição.
/// </summary>
public sealed record PurviewServiceResultCompletenessAssessment(
    PurviewServiceResultCompletenessOutcome Outcome, int CanonicalCount, int MatchedCount, int ReportVersion);

/// <summary>
/// Avalia a completude da evidência do provider para um plano de import job (AB-I6-001 item 12) —
/// SOMENTE leitura, nunca produz efeito colateral. Reidrata a versão mais recente do service result report
/// (revalidação de integridade fail-closed) e correlaciona contra a cadeia canônica ATUAL da onda; um
/// drift desde a importação é recusado (força reimportação) em vez de avaliar contra evidência obsoleta.
/// NUNCA retorna <c>PASS</c>/certificate/conclusão de onda — apenas
/// <see cref="PurviewServiceResultCompletenessOutcome"/>.
/// </summary>
public sealed class EvaluatePurviewServiceResultCompletenessUseCase(
    ResolvePurviewMappingEvidenceUseCase evidenceResolver,
    IPurviewMappingCsvStore mappings,
    IPurviewImportJobStore jobs,
    IPurviewServiceResultReportStore reports)
{
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IPurviewImportJobStore _jobs = jobs;
    private readonly IPurviewServiceResultReportStore _reports = reports;

    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, ou drift desde a importação do relatório.</exception>
    /// <exception cref="PurviewServiceResultCorrelationException">Evidência persistida não correlaciona mais 1:1 com a cadeia canônica ATUAL.</exception>
    public async Task<PurviewServiceResultCompletenessAssessment> ExecuteAsync(
        TenantScope scope, WaveId waveId, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        _ = await _jobs.GetPlanByNameAsync(scope, waveId, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException(
                "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        var latest = await _reports.GetLatestAsync(scope, waveId, plannedJobName, cancellationToken).ConfigureAwait(false);
        var check = await PurviewImportJobEvidenceGuard
            .ResolveAndVerifyNoDriftAsync(_evidenceResolver, _mappings, scope, waveId, cancellationToken)
            .ConfigureAwait(false);

        if (latest is null)
        {
            // Nenhum relatório importado ainda: nenhum PST canônico foi coberto — Incomplete, nunca um erro.
            return new PurviewServiceResultCompletenessAssessment(
                PurviewServiceResultCompletenessOutcome.Incomplete, check.CanonicalRemoteNames.Count, MatchedCount: 0, ReportVersion: 0);
        }

        var rows = await _reports.GetRowsAsync(scope, waveId, plannedJobName, latest.ReportVersion, cancellationToken).ConfigureAwait(false);
        var correlation = PurviewServiceResultCorrelation.Correlate(
            check.CanonicalRemoteNames, rows, reportDeclaresCompleteness: latest.DeclaredTotalRows.HasValue);
        var outcome = PurviewServiceResultCompleteness.Evaluate(correlation);

        return new PurviewServiceResultCompletenessAssessment(outcome, correlation.CanonicalCount, correlation.MatchedCount, latest.ReportVersion);
    }
}
