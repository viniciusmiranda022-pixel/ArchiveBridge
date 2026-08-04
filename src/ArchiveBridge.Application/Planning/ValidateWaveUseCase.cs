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
/// (100 GB decimais) sobre a seleção da onda. Se QUALQUER archive exigir avaliação E ainda não houver
/// decisão Microsoft aprovada para a versão corrente, a onda fica <c>Blocked</c> (com
/// <c>MICROSOFT_ASSESSMENT_REQUIRED</c>); um archive acima do limite com decisão aprovada é tratado
/// como LIBERADO. As avaliações e a transição de estado são gravadas ATOMICAMENTE (uma transação, com
/// concorrência otimista e cercamento do Job quando durável) — sem estado parcial. É idempotente em
/// retry: se a onda já está Blocked/ReadyForApproval, não re-registra. Uma seleção com identidade de
/// archive NÃO resolvida é recusada (fail-closed). As razões são livres de PII.
/// </summary>
public sealed class ValidateWaveUseCase(IWaveStore waves, ICapacityAssessmentDecisionStore decisions, IClock clock)
{
    private const string AssessmentRequiredReason =
        "Volume planejado por archive excede 100 GB; avaliação Microsoft exigida antes de prosseguir.";

    private const string AssessmentReleasedReason =
        "Volume planejado por archive excede 100 GB; liberado por decisão de avaliação Microsoft aprovada.";

    private const string WithinLimitReason =
        "Volume planejado por archive dentro do limite de 100 GB.";

    private const string ReleasedRuleCode = "MICROSOFT_ASSESSMENT_RELEASED";

    private readonly IWaveStore _waves = waves;
    private readonly ICapacityAssessmentDecisionStore _decisions = decisions;
    private readonly IClock _clock = clock;

    /// <summary>Executa a validação de capacidade da onda. Quando <paramref name="fence"/> é informado, o efeito é cercado pelo Job.</summary>
    public async Task<WaveValidationResult> ExecuteAsync(
        TenantScope scope, WaveId waveId, CorrelationId correlation, CancellationToken cancellationToken, JobFence? fence = null)
    {
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        // Fail-closed: sem resolvedor de diretório real, uma identidade de archive não resolvida
        // (derivada só da mailbox) não pode ser validada — aliases poderiam mascarar o volume real.
        if (wave.Selection.HasUnresolvedArchive)
        {
            throw new PlanningValidationException(
                "A seleção contém archive com identidade não resolvida; validação recusada (exija identidade do manifesto autorizado).");
        }

        var report = CapacityPlanner.Assess(wave.Selection);

        // Idempotência em retry: já validada para esta versão — não re-registra nem re-transita.
        if (wave.Status is WaveStatus.Blocked or WaveStatus.ReadyForApproval)
        {
            return new WaveValidationResult(wave.Status, report.AssessmentRequired, report.PerArchive.Count);
        }

        var released = await _decisions.GetApprovedAsync(scope, waveId, wave.Version, cancellationToken).ConfigureAwait(false);
        var blockRequired = report.PerArchive.Any(archive =>
            archive.Result == CapacityAssessmentResult.AssessmentRequired && !released.ContainsKey(archive.Archive));

        var now = _clock.UtcNow;
        var assessments = BuildAssessments(report, released, correlation, now);

        MoveToValidating(wave);
        if (blockRequired)
        {
            wave.Block();
        }
        else
        {
            wave.MarkReadyForApproval();
        }

        await _waves.SaveValidationAsync(wave, assessments, correlation, cancellationToken, fence).ConfigureAwait(false);

        return new WaveValidationResult(wave.Status, blockRequired, report.PerArchive.Count);
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
        CapacityAssessmentReport report,
        IReadOnlyDictionary<TargetArchiveId, string> released,
        CorrelationId correlation,
        DateTimeOffset now)
    {
        var assessments = new List<PlanningAssessment>(report.PerArchive.Count);
        foreach (var archive in report.PerArchive)
        {
            if (archive.Result == CapacityAssessmentResult.AssessmentRequired
                && released.TryGetValue(archive.Archive, out var releasedBy))
            {
                assessments.Add(new PlanningAssessment(
                    archive.Archive, archive.TotalBytes, ReleasedRuleCode, CapacityAssessmentResult.AssessmentRequired,
                    AssessmentReleasedReason, correlation, now, releasedBy));
                continue;
            }

            var reason = archive.Result == CapacityAssessmentResult.AssessmentRequired
                ? AssessmentRequiredReason
                : WithinLimitReason;
            assessments.Add(new PlanningAssessment(
                archive.Archive, archive.TotalBytes, archive.RuleCode, archive.Result,
                reason, correlation, now, ReleasedBy: null));
        }

        return assessments;
    }
}
