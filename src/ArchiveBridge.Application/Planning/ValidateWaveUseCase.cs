using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>Resultado de <see cref="ValidateWaveUseCase"/>.</summary>
public sealed record WaveValidationResult(WaveStatus Status, bool AssessmentRequired, int ArchiveCount);

/// <summary>
/// Corpo de execução do comando durável <c>ValidateWave</c>. Aplica a regra de capacidade por archive
/// (100 GB decimais) sobre a seleção da onda. Se QUALQUER archive exigir avaliação, a onda fica
/// <c>Blocked</c> (com <c>MICROSOFT_ASSESSMENT_REQUIRED</c>) até evidência/decisão; caso contrário,
/// avança para <c>ReadyForApproval</c>. Cada avaliação é registrada como evidência (append-only). É
/// idempotente em retry: se a onda já está Blocked/ReadyForApproval (validada para esta versão), não
/// re-registra. As razões são livres de PII (não citam mailbox, caminho ou nome de PST).
/// </summary>
public sealed class ValidateWaveUseCase(IWaveStore waves, IPlanningStore planning, IClock clock)
{
    private const string AssessmentRequiredReason =
        "Volume planejado por archive excede 100 GB; avaliação Microsoft exigida antes de prosseguir.";

    private const string WithinLimitReason =
        "Volume planejado por archive dentro do limite de 100 GB.";

    private readonly IWaveStore _waves = waves;
    private readonly IPlanningStore _planning = planning;
    private readonly IClock _clock = clock;

    /// <summary>Executa a validação de capacidade da onda.</summary>
    public async Task<WaveValidationResult> ExecuteAsync(
        TenantScope scope, WaveId waveId, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        var report = CapacityPlanner.Assess(wave.Selection);

        // Idempotência em retry: já validada para esta versão — não re-registra nem re-transita.
        if (wave.Status is WaveStatus.Blocked or WaveStatus.ReadyForApproval)
        {
            return new WaveValidationResult(wave.Status, report.AssessmentRequired, report.PerArchive.Count);
        }

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

        await _planning.RecordAsync(scope, waveId, wave.Version, assessments, cancellationToken).ConfigureAwait(false);
        await _waves.SaveStatusAsync(wave, correlation, cancellationToken).ConfigureAwait(false);

        return new WaveValidationResult(wave.Status, report.AssessmentRequired, report.PerArchive.Count);
    }

    private static void MoveToValidating(MigrationWave wave)
    {
        switch (wave.Status)
        {
            case WaveStatus.Draft:
                wave.StartValidation();
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
                archive.Archive,
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
