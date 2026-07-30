using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>Resultado de <see cref="ValidateWaveUseCase"/>.</summary>
public sealed record WaveValidationResult(JobId Job, WaveStatus Status, bool AssessmentRequired, int ArchiveCount);

/// <summary>
/// Comando ValidateWave: aplica a regra de capacidade por archive (100 GiB) sobre a seleção da onda.
/// Se QUALQUER archive exigir avaliação, a onda fica <c>Blocked</c> (com <c>MICROSOFT_ASSESSMENT_REQUIRED</c>)
/// até evidência/decisão; caso contrário, avança para <c>ReadyForApproval</c>. Cada avaliação é
/// registrada como evidência (append-only). Emite um Job de controle durável correlacionado. As
/// razões registradas são livres de PII (não citam mailbox, caminho ou nome de PST).
/// </summary>
public sealed class ValidateWaveUseCase(IWaveStore waves, IPlanningStore planning, IJobStore jobs, IClock clock)
{
    private const string AssessmentRequiredReason =
        "Volume planejado por archive excede 100 GiB; avaliação Microsoft exigida antes de prosseguir.";

    private const string WithinLimitReason =
        "Volume planejado por archive dentro do limite de 100 GiB.";

    private readonly IWaveStore _waves = waves;
    private readonly IPlanningStore _planning = planning;
    private readonly IJobStore _jobs = jobs;
    private readonly IClock _clock = clock;

    /// <summary>Executa a validação de capacidade da onda.</summary>
    public async Task<WaveValidationResult> ExecuteAsync(
        TenantScope scope, WaveId waveId, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        var report = CapacityPlanner.Assess(wave.Selection);
        var now = _clock.UtcNow;
        var assessments = BuildAssessments(report, correlation, now);

        MoveToValidating(wave);
        if (report.AssessmentRequired)
        {
            wave.Block();
        }
        else
        {
            wave.MarkReadyForApproval();
        }

        var jobId = await _jobs.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, JobPriority.Normal, correlation), cancellationToken)
            .ConfigureAwait(false);
        await _planning.RecordAsync(scope, waveId, wave.Version, assessments, cancellationToken).ConfigureAwait(false);
        await _waves.SaveStatusAsync(wave, correlation, cancellationToken).ConfigureAwait(false);

        return new WaveValidationResult(jobId, wave.Status, report.AssessmentRequired, report.PerArchive.Count);
    }

    private static void MoveToValidating(MigrationWave wave)
    {
        switch (wave.Status)
        {
            case WaveStatus.Draft:
                wave.StartValidation();
                break;
            case WaveStatus.Blocked:
                wave.Unblock();
                break;
            case WaveStatus.Validating:
                break;
            default:
                throw new PlanningValidationException(
                    $"Onda no estado {wave.Status} não pode ser (re)validada.");
        }
    }

    private static List<PlanningAssessment> BuildAssessments(
        CapacityAssessmentReport report, CorrelationId correlation, DateTimeOffset now)
    {
        var assessments = new List<PlanningAssessment>(report.PerArchive.Count);
        foreach (var archive in report.PerArchive)
        {
            var reason = archive.Result == CapacityAssessmentResult.AssessmentRequired
                ? AssessmentRequiredReason
                : WithinLimitReason;
            assessments.Add(new PlanningAssessment(
                archive.Mailbox,
                archive.TotalBytes,
                archive.RuleCode,
                archive.Result,
                reason,
                correlation,
                now,
                ReleasedBy: null));
        }

        return assessments;
    }
}
