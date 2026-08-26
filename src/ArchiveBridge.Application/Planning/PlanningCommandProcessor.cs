using ArchiveBridge.Application.Jobs;
using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Application.Waves;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>Desfecho do processamento de um comando durável.</summary>
public enum PlanningCommandOutcome
{
    /// <summary>Executado e o Job concluído (efeito aplicado ou replay idempotente válido).</summary>
    Completed,

    /// <summary>
    /// Erro de domínio/contexto obsoleto OU orçamento de retry esgotado (AB-I7-002: concorrência
    /// persistente que nunca se resolve converge a Failed em vez de retry indefinido): Job falhado de
    /// forma terminal.
    /// </summary>
    Failed,

    /// <summary>Erro transitório COM orçamento de retry disponível: nova tentativa agendada (retry).</summary>
    Retried,

    /// <summary>
    /// O cercamento (fencing) foi perdido: outra época/worker é dona do Job. Nenhum efeito deste
    /// worker foi persistido e a conclusão NÃO é reivindicada — não é sucesso.
    /// </summary>
    Fenced,
}

/// <summary>Resultado do processamento de um comando reivindicado.</summary>
public sealed record PlanningCommandExecution(
    JobId Job, PlanningCommandKind Kind, PlanningCommandOutcome Outcome, JobCommandOutcome JobOutcome);

/// <summary>
/// Consumidor durável dos comandos de planejamento: reivindica o próximo Job de planejamento (claim
/// EXCLUSIVO — só Jobs com contexto em <c>planning_commands</c>, com fencing por época), confere que o
/// contexto do comando ainda corresponde ao estado corrente do agregado (senão o comando é obsoleto e
/// falha com <c>STALE_COMMAND_CONTEXT</c>), executa a operação com os efeitos CERCADOS pelo mesmo
/// fencing do Job e conclui — ou, em erro de domínio, falha terminalmente; em erro transitório, agenda
/// retry. Se o cercamento é perdido durante os efeitos, nenhum efeito é persistido e a conclusão não é
/// reivindicada (<see cref="PlanningCommandOutcome.Fenced"/>). Uma conclusão só é reportada como
/// <see cref="PlanningCommandOutcome.Completed"/> quando <c>CompleteAsync</c> aplica de fato (ou é
/// replay idempotente válido) — nunca em <c>FencedOut</c>/<c>NotFound</c>.
/// </summary>
public sealed class PlanningCommandProcessor(
    IPlanningCommandInbox queue,
    IJobStore jobs,
    IJobLeaseManager leases,
    IProjectStore projects,
    IWaveStore waves,
    ValidateProjectUseCase validateProject,
    ValidateWaveUseCase validateWave,
    GenerateMappingCsvUseCase generateMapping,
    FreezeWaveUseCase freezeWave,
    MappingPolicy policy,
    IClock clock,
    RetryPolicy retryPolicy)
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly IPlanningCommandInbox _queue = queue;
    private readonly IJobStore _jobs = jobs;
    private readonly RetryPolicy _retryPolicy = retryPolicy;
    private readonly PlanningHeartbeat _heartbeat = new(leases);
    private readonly IProjectStore _projects = projects;
    private readonly IWaveStore _waves = waves;
    private readonly ValidateProjectUseCase _validateProject = validateProject;
    private readonly ValidateWaveUseCase _validateWave = validateWave;
    private readonly GenerateMappingCsvUseCase _generateMapping = generateMapping;
    private readonly FreezeWaveUseCase _freezeWave = freezeWave;
    private readonly MappingPolicy _policy = policy;
    private readonly IClock _clock = clock;

    /// <summary>Reivindica e processa o próximo comando; <see langword="null"/> se não houver trabalho.</summary>
    public async Task<PlanningCommandExecution?> ProcessNextAsync(
        TenantScope scope, WorkerId worker, TimeSpan leaseDuration, CorrelationId correlation, CancellationToken cancellationToken)
    {
        var claimed = await _queue.TryClaimNextAsync(scope, worker, leaseDuration, correlation, cancellationToken)
            .ConfigureAwait(false);
        if (claimed is null)
        {
            return null;
        }

        var command = claimed.Command;
        var lease = new LeaseCommand(scope, claimed.Job.JobId, worker, claimed.Job.Epoch, command.Correlation);
        var fence = new JobFence(scope, claimed.Job.JobId, worker, claimed.Job.Epoch);

        try
        {
            await GuardContextAsync(command, cancellationToken).ConfigureAwait(false);

            // Heartbeat PERIÓDICO real: renova o lease repetidamente (intervalo = lease/3) DURANTE toda a
            // execução da operação, mantendo válida uma operação mais longa que o lease. Um batimento
            // perdido (época/dono defasado, lease vencido ou exceção transitória) cancela a operação —
            // nenhum efeito novo é iniciado — e resulta em Fenced (a conclusão NÃO é reivindicada). O
            // laço para assim que a operação termina e é aguardado (sem Task de batimento órfã).
            var beat = await _heartbeat.RunWhileAsync(
                lease,
                HeartbeatInterval(leaseDuration),
                token => DispatchAsync(command, fence, token),
                cancellationToken).ConfigureAwait(false);
            if (beat.Lost)
            {
                return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, PlanningCommandOutcome.Fenced, beat.LastOutcome);
            }
        }
        catch (FencedOutException)
        {
            // Cercamento perdido durante os efeitos: outra época é dona; nenhum efeito foi persistido e
            // não reivindicamos conclusão nem falha (seriam recusadas por fencing de qualquer modo).
            return new PlanningCommandExecution(
                claimed.Job.JobId, command.Kind, PlanningCommandOutcome.Fenced, JobCommandOutcome.FencedOut);
        }
        catch (Exception exception) when (IsTerminal(exception))
        {
            var failed = await _jobs.FailAsync(lease, ErrorCode.Validation, cancellationToken).ConfigureAwait(false);
            return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, OutcomeFor(failed, PlanningCommandOutcome.Failed), failed);
        }
        catch (ConcurrencyException)
        {
            var gate = await JobRetryGate.ScheduleRetryOrFailAsync(
                _jobs, _clock, _retryPolicy, lease, ErrorCode.ConcurrencyLost, RetryBackoff, cancellationToken).ConfigureAwait(false);
            var gatedOutcome = gate.RetryScheduled ? PlanningCommandOutcome.Retried : PlanningCommandOutcome.Failed;
            return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, OutcomeFor(gate.Outcome, gatedOutcome), gate.Outcome);
        }

        var completed = await _jobs.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
        return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, OutcomeFor(completed, PlanningCommandOutcome.Completed), completed);
    }

    // Intervalo do batimento: menor que o lease (lease/3), com piso positivo para nunca virar um laço
    // de espera ocupada. Renovar a lease/3 dá margem para ≥2 renovações antes de qualquer expiração.
    private static TimeSpan HeartbeatInterval(TimeSpan leaseDuration) =>
        leaseDuration > TimeSpan.Zero ? leaseDuration / 3.0 : TimeSpan.FromMilliseconds(1);

    // B4: só reporta a transição (Completed/Failed/Retried) quando o comando de Job foi de fato aplicado
    // (ou replay idempotente válido). FencedOut/NotFound ⇒ Fenced — nunca uma transição "aplicada".
    private static PlanningCommandOutcome OutcomeFor(JobCommandOutcome jobOutcome, PlanningCommandOutcome appliedOutcome) =>
        jobOutcome is JobCommandOutcome.Applied or JobCommandOutcome.IdempotentReplay
            ? appliedOutcome
            : PlanningCommandOutcome.Fenced;

    private async Task GuardContextAsync(PlanningCommand command, CancellationToken cancellationToken)
    {
        // Contexto obrigatório por tipo (fail-closed) + schema conhecido, antes de carregar o agregado.
        PlanningCommandValidation.EnsureValid(command);
        switch (command.Kind)
        {
            case PlanningCommandKind.ValidateProject:
                var project = await _projects.GetAsync(command.Scope, cancellationToken).ConfigureAwait(false)
                    ?? throw new PlanningNotFoundException("Projeto não encontrado no escopo.");
                PlanningCommandContextGuard.EnsureProject(command.Context, project);
                break;
            case PlanningCommandKind.ValidateWave:
            case PlanningCommandKind.FreezeWave:
                var wave = await LoadWaveAsync(command, cancellationToken).ConfigureAwait(false);
                PlanningCommandContextGuard.EnsureWave(command.Context, wave, requireMapping: false);
                break;
            case PlanningCommandKind.GenerateMappingCsv:
                var mappingWave = await LoadWaveAsync(command, cancellationToken).ConfigureAwait(false);
                PlanningCommandContextGuard.EnsureWave(command.Context, mappingWave, requireMapping: true);
                PlanningCommandContextGuard.EnsureMappingPolicy(command.Context, _policy);
                break;
            default:
                throw new PlanningValidationException($"Tipo de comando de planejamento desconhecido: {command.Kind}.");
        }
    }

    private async Task<MigrationWave> LoadWaveAsync(PlanningCommand command, CancellationToken cancellationToken) =>
        await _waves.GetAsync(command.Scope, RequireWave(command), cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

    private async Task DispatchAsync(PlanningCommand command, JobFence fence, CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case PlanningCommandKind.ValidateProject:
                await _validateProject.ExecuteAsync(command.Scope, command.Correlation, cancellationToken, fence).ConfigureAwait(false);
                break;
            case PlanningCommandKind.ValidateWave:
                await _validateWave.ExecuteAsync(command.Scope, RequireWave(command), command.Correlation, cancellationToken, fence).ConfigureAwait(false);
                break;
            case PlanningCommandKind.GenerateMappingCsv:
                await _generateMapping.ExecuteAsync(
                    command.Scope, RequireWave(command), new ContentCodePage(RequireCodePage(command)),
                    _policy, RequireGeneratedBy(command), command.Correlation, cancellationToken, fence).ConfigureAwait(false);
                break;
            case PlanningCommandKind.FreezeWave:
                await _freezeWave.ExecuteAsync(command.Scope, RequireWave(command), command.Correlation, cancellationToken, fence).ConfigureAwait(false);
                break;
            default:
                throw new PlanningValidationException($"Tipo de comando de planejamento desconhecido: {command.Kind}.");
        }
    }

    private static bool IsTerminal(Exception exception) =>
        exception is PlanningValidationException
            or PlanningNotFoundException
            or StaleCommandContextException
            or MappingGenerationException
            or MappingCsvInjectionException
            or MappingCsvFormatException
            or InvalidWaveTransitionException
            or InvalidProjectTransitionException
            or ArgumentException;

    private static WaveId RequireWave(PlanningCommand command) =>
        command.Wave ?? throw new PlanningValidationException("Comando exige uma onda, mas nenhuma foi informada.");

    private static int RequireCodePage(PlanningCommand command) =>
        command.ContentCodePage ?? throw new PlanningValidationException("Comando de mapping exige ContentCodePage.");

    private static string RequireGeneratedBy(PlanningCommand command) =>
        command.GeneratedBy ?? throw new PlanningValidationException("Comando de mapping exige generatedBy.");
}
