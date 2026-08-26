using ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Application.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.TargetIngestion.Purview.ExoStatistics;
using ArchiveBridge.Domain.TargetIngestion.Purview.Reconciliation;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.Reconciliation;

/// <summary>
/// Computa e persiste a próxima versão (idempotente) da avaliação de reconciliação expected-vs-observed de
/// uma wave (AB-I6-007) a partir SOMENTE de evidências canônicas já persistidas e revalidadas — nunca de
/// path/mailbox/contador/lista fornecidos pelo caller (item 2).
/// <para>
/// ANTES de qualquer cálculo, revalida a cadeia canônica INTEIRA (mapping/binding/execução/upload) sem
/// drift via <see cref="ServiceResult.PurviewImportJobEvidenceGuard"/> — o MESMO guard reaproveitado pelos
/// Passos 1/2 (item 4/12: "evidência válida no passado não pode produzir avaliação canônica se
/// mapping/upload/binding/execution atuais divergirem"). Esta é também a ÚNICA forma sancionada de tratar
/// uma avaliação como canônica: uma leitura direta de <see cref="IReconciliationAssessmentStore.GetLatestAsync"/>
/// nunca revalida drift por si só (mesmo desenho de <see cref="EvaluatePurviewServiceResultCompletenessUseCase"/>),
/// então qualquer drift real na cadeia canônica desde a última avaliação bloqueia fail-closed uma NOVA
/// reconciliação em vez de devolver silenciosamente a avaliação antiga como se ainda fosse vigente.
/// </para>
/// <para>
/// O conjunto OBSERVADO consome exclusivamente a versão mais recente já revalidada do service result
/// report do Purview (Passo 1) e os snapshots <c>BeforeImport</c>/<c>AfterImport</c> mais recentes já
/// revalidados de cada archive esperado (Passo 2) — nunca sonda adapter algum, nunca escreve em
/// EXO/Graph/Purview/EV, nunca emite certificate ou fecha onda/projeto (STOP-THE-LINE).
/// </para>
/// </summary>
public sealed class EvaluateReconciliationUseCase(
    ResolvePurviewMappingEvidenceUseCase evidenceResolver,
    IPurviewMappingCsvStore mappings,
    IPurviewImportJobStore jobs,
    IPurviewServiceResultReportStore reports,
    IExoArchiveStatisticsStore snapshots,
    IReconciliationAssessmentStore assessments,
    IClock clock)
{
    private readonly ResolvePurviewMappingEvidenceUseCase _evidenceResolver = evidenceResolver;
    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IPurviewImportJobStore _jobs = jobs;
    private readonly IPurviewServiceResultReportStore _reports = reports;
    private readonly IExoArchiveStatisticsStore _snapshots = snapshots;
    private readonly IReconciliationAssessmentStore _assessments = assessments;
    private readonly IClock _clock = clock;

    /// <exception cref="PurviewImportJobSourceNotFoundException">Onda/plano inexistente ou fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewImportJobPrerequisiteException">Upload/mapping não canônico, ou drift desde a última avaliação/importação/captura.</exception>
    /// <exception cref="ReconciliationValidationException">O service result observado contém duas linhas com o mesmo nome remoto (fail-closed).</exception>
    public async Task<ReconciliationAssessment> ExecuteAsync(
        TenantScope scope,
        WaveId waveId,
        PurviewImportJobName plannedJobName,
        CorrelationId correlation,
        CancellationToken cancellationToken,
        JobFence? fence = null)
    {
        _ = await _jobs.GetPlanByNameAsync(scope, waveId, plannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException(
                "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        // Item 4/12: revalida a cadeia canônica INTEIRA sem drift, SEMPRE, antes de qualquer cálculo —
        // nunca depois, nunca condicionalmente.
        var check = await PurviewImportJobEvidenceGuard
            .ResolveAndVerifyNoDriftAsync(_evidenceResolver, _mappings, scope, waveId, cancellationToken)
            .ConfigureAwait(false);

        // Conjunto observado (PST): SOMENTE a versão mais recente já revalidada do service result report
        // (Passo 1) — nenhum contador/status é sondado de outra fonte.
        var latestReport = await _reports.GetLatestAsync(scope, waveId, plannedJobName, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PurviewServiceResultRow> observedRows = latestReport is null
            ? []
            : await _reports.GetRowsAsync(scope, waveId, plannedJobName, latestReport.ReportVersion, cancellationToken).ConfigureAwait(false);

        var pstItems = ReconciliationPstCorrelation.Correlate(check.CanonicalRemoteNames, observedRows);

        // Conjunto observado (archive): SOMENTE os snapshots BeforeImport/AfterImport mais recentes já
        // revalidados (Passo 2) de cada archive PRESENTE no conjunto esperado ATUAL — nunca um archive
        // fornecido pelo caller.
        var expectedArchives = check.Evidence.Rows
            .Select(row => row.Entry.Archive.Identity)
            .Distinct()
            .OrderBy(archive => archive.Value, StringComparer.Ordinal)
            .ToList();

        var archiveItems = new List<ArchiveReconciliationItem>(expectedArchives.Count);
        var archiveEvidence = new List<ReconciliationArchiveEvidenceRef>(expectedArchives.Count);
        foreach (var archive in expectedArchives)
        {
            var before = await _snapshots.GetLatestAsync(scope, waveId, archive, ExoStatisticsPhase.BeforeImport, cancellationToken).ConfigureAwait(false);
            var after = await _snapshots.GetLatestAsync(scope, waveId, archive, ExoStatisticsPhase.AfterImport, cancellationToken).ConfigureAwait(false);

            archiveItems.Add(ReconciliationArchiveCorrelation.Correlate(archive, before, after));
            archiveEvidence.Add(new ReconciliationArchiveEvidenceRef(
                archive, before?.SnapshotVersion, before?.ObservationHash, after?.SnapshotVersion, after?.ObservationHash));
        }

        return await _assessments.PersistAsync(
            scope,
            waveId,
            plannedJobName,
            check.Fingerprint.Value,
            latestReport?.ReportVersion,
            latestReport?.ContentSha256,
            archiveEvidence,
            pstItems,
            archiveItems,
            correlation,
            _clock.UtcNow,
            fence,
            cancellationToken).ConfigureAwait(false);
    }
}
