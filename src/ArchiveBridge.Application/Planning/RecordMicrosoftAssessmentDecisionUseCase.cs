using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Planning;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>
/// Comando de decisão de avaliação Microsoft: identifica a onda, a VERSÃO corrente (a decisão nunca se
/// aplica a outra versão), o archive avaliado (identidade canônica), o desfecho, a referência de
/// evidência, o responsável (nunca anônimo), a correlação e notas sem PII.
/// </summary>
public sealed record AssessmentDecisionCommand(
    TenantScope Scope,
    WaveId Wave,
    int WaveVersion,
    TargetArchiveId Archive,
    MicrosoftAssessmentDecision Decision,
    string EvidenceReference,
    string DecidedBy,
    CorrelationId Correlation,
    string? Notes);

/// <summary>Resultado da decisão: o estado resultante da onda.</summary>
public sealed record AssessmentDecisionResult(WaveStatus Status);

/// <summary>
/// Registra (append-only) uma decisão de avaliação Microsoft e, quando aprovada, LIBERA a onda
/// bloqueada por capacidade. A decisão só é aceita se: houver responsável e referência de evidência;
/// a onda estiver <c>Blocked</c>; a versão da decisão for a CORRENTE da onda; e o archive avaliado
/// pertencer à seleção. Uma aprovação grava ATOMICAMENTE a decisão + a transição <c>Blocked → Validating</c>
/// (concorrência otimista) e revalida a onda — que agora trata o archive como liberado. Uma rejeição
/// apenas registra a evidência (a onda permanece bloqueada). Nunca altera nem apaga a avaliação
/// anterior. Cross-tenant/cross-projeto são barrados pelo escopo/RLS.
/// </summary>
public sealed class RecordMicrosoftAssessmentDecisionUseCase(
    IWaveStore waves, ICapacityAssessmentDecisionStore decisions, ValidateWaveUseCase revalidate, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly ICapacityAssessmentDecisionStore _decisions = decisions;
    private readonly ValidateWaveUseCase _revalidate = revalidate;
    private readonly IClock _clock = clock;

    /// <summary>Executa a decisão de avaliação.</summary>
    public async Task<AssessmentDecisionResult> ExecuteAsync(AssessmentDecisionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var decidedBy = RequireText(command.DecidedBy, "responsável pela decisão");
        var evidence = RequireText(command.EvidenceReference, "referência de evidência");

        var wave = await _waves.GetAsync(command.Scope, command.Wave, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        if (wave.Status != WaveStatus.Blocked)
        {
            throw new PlanningValidationException(
                $"Só é possível decidir a avaliação de uma onda bloqueada; estado atual: {wave.Status}.");
        }

        if (command.WaveVersion != wave.Version.Value)
        {
            throw new PlanningValidationException(
                "A decisão refere-se a uma versão que não é a corrente da onda; recusada (fail-closed).");
        }

        if (!wave.Selection.Entries.Any(entry => entry.Archive.Identity == command.Archive))
        {
            throw new PlanningValidationException(
                "O archive da decisão não pertence à seleção corrente da onda; recusado.");
        }

        var now = _clock.UtcNow;
        var decision = new CapacityAssessmentDecision(
            command.Archive, wave.Version, command.Decision, evidence, decidedBy, now, command.Correlation, command.Notes);

        var unblock = command.Decision == MicrosoftAssessmentDecision.Approved;
        if (unblock)
        {
            wave.Unblock();
        }

        await _decisions.RecordAsync(wave, decision, unblock, fence: null, cancellationToken).ConfigureAwait(false);

        if (!unblock)
        {
            return new AssessmentDecisionResult(wave.Status);
        }

        // Revalida considerando a decisão registrada: o archive liberado deixa de bloquear.
        var revalidated = await _revalidate
            .ExecuteAsync(command.Scope, command.Wave, command.Correlation, cancellationToken).ConfigureAwait(false);
        return new AssessmentDecisionResult(revalidated.Status);
    }

    private static string RequireText(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PlanningValidationException($"Decisão sem {what} não é permitida (obrigatório).");
        }

        return value.Trim();
    }
}
