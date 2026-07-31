using ArchiveBridge.Application.Mapping;
using ArchiveBridge.Application.Waves;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Planning;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Planning;

/// <summary>Desfecho do processamento de um comando durável.</summary>
public enum PlanningCommandOutcome
{
    /// <summary>Executado e o Job concluído.</summary>
    Completed,

    /// <summary>Erro de domínio (dado inválido): Job falhado de forma terminal.</summary>
    Failed,

    /// <summary>Erro transitório: nova tentativa agendada (retry).</summary>
    Retried,
}

/// <summary>Resultado do processamento de um comando reivindicado.</summary>
public sealed record PlanningCommandExecution(
    JobId Job, PlanningCommandKind Kind, PlanningCommandOutcome Outcome, JobCommandOutcome JobOutcome);

/// <summary>
/// Consumidor durável dos comandos de planejamento: reivindica o próximo Job de controle (com fencing
/// por época), lê o seu contexto, executa a operação correspondente e <b>conclui</b> o Job — ou, em
/// erro de domínio, o <b>falha</b> terminalmente; em erro transitório, agenda <b>retry</b>. Os corpos
/// de execução são idempotentes, então a reexecução após queda (lease recuperado) converge sem
/// duplicar efeito. Não há mais Job Pending decorativo: todo comando é criado pela fila e concluído
/// aqui.
/// </summary>
public sealed class PlanningCommandProcessor(
    IPlanningCommandInbox queue,
    IJobStore jobs,
    ValidateProjectUseCase validateProject,
    ValidateWaveUseCase validateWave,
    GenerateMappingCsvUseCase generateMapping,
    FreezeWaveUseCase freezeWave,
    MappingPolicy policy,
    IClock clock)
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly IPlanningCommandInbox _queue = queue;
    private readonly IJobStore _jobs = jobs;
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

        try
        {
            await DispatchAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTerminal(exception))
        {
            var failed = await _jobs.FailAsync(lease, ErrorCode.Validation, cancellationToken).ConfigureAwait(false);
            return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, PlanningCommandOutcome.Failed, failed);
        }
        catch (ConcurrencyException)
        {
            var retried = await _jobs.ScheduleRetryAsync(
                lease, ErrorCode.ConcurrencyLost, _clock.UtcNow + RetryBackoff, cancellationToken).ConfigureAwait(false);
            return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, PlanningCommandOutcome.Retried, retried);
        }

        var completed = await _jobs.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
        return new PlanningCommandExecution(claimed.Job.JobId, command.Kind, PlanningCommandOutcome.Completed, completed);
    }

    private async Task DispatchAsync(PlanningCommand command, CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case PlanningCommandKind.ValidateProject:
                await _validateProject.ExecuteAsync(command.Scope, command.Correlation, cancellationToken).ConfigureAwait(false);
                break;
            case PlanningCommandKind.ValidateWave:
                await _validateWave.ExecuteAsync(command.Scope, RequireWave(command), command.Correlation, cancellationToken).ConfigureAwait(false);
                break;
            case PlanningCommandKind.GenerateMappingCsv:
                await _generateMapping.ExecuteAsync(
                    command.Scope, RequireWave(command), new ContentCodePage(RequireCodePage(command)),
                    _policy, RequireGeneratedBy(command), command.Correlation, cancellationToken).ConfigureAwait(false);
                break;
            case PlanningCommandKind.FreezeWave:
                await _freezeWave.ExecuteAsync(command.Scope, RequireWave(command), command.Correlation, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new PlanningValidationException($"Tipo de comando de planejamento desconhecido: {command.Kind}.");
        }
    }

    private static bool IsTerminal(Exception exception) =>
        exception is PlanningValidationException
            or PlanningNotFoundException
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
