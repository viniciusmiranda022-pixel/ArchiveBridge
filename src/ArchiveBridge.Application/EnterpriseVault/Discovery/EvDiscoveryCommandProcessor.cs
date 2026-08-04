using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Projects;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Application.EnterpriseVault.Discovery;

/// <summary>Desfecho do processamento de um comando durável de descoberta.</summary>
public enum EvDiscoveryCommandOutcome
{
    /// <summary>Executado e o Job concluído (efeito aplicado ou replay idempotente válido).</summary>
    Completed,

    /// <summary>Erro de domínio/contexto obsoleto: Job falhado de forma terminal.</summary>
    Failed,

    /// <summary>Erro transitório (sonda/concorrência): nova tentativa agendada.</summary>
    Retried,

    /// <summary>Cercamento perdido: nenhum efeito persistido e a conclusão NÃO é reivindicada.</summary>
    Fenced,
}

/// <summary>Resultado do processamento de um comando de descoberta reivindicado.</summary>
public sealed record EvDiscoveryCommandExecution(JobId Job, EvDiscoveryCommandOutcome Outcome, JobCommandOutcome JobOutcome);

/// <summary>
/// Consumidor durável dos comandos de descoberta EV: reivindica o próximo Job de descoberta (claim
/// EXCLUSIVO por workload <see cref="Workload.EnterpriseVault"/> com contexto em
/// <c>ev_discovery_commands</c> e fencing por época), confere que o contexto ainda corresponde ao estado
/// corrente (senão <c>STALE_COMMAND_CONTEXT</c>), executa a descoberta READ-ONLY com HEARTBEAT PERIÓDICO
/// e efeitos CERCADOS pelo mesmo fencing, e conclui — ou falha/retry. Se o cercamento é perdido durante
/// os efeitos, nenhum efeito é persistido e a conclusão não é reivindicada
/// (<see cref="EvDiscoveryCommandOutcome.Fenced"/>).
/// </summary>
public sealed class EvDiscoveryCommandProcessor(
    IEvDiscoveryCommandInbox queue,
    IJobStore jobs,
    IJobLeaseManager leases,
    IProjectStore projects,
    DiscoverEvCapabilitiesUseCase discover,
    EvDiscoveryPolicy policy,
    IClock clock)
{
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly IEvDiscoveryCommandInbox _queue = queue;
    private readonly IJobStore _jobs = jobs;
    private readonly PlanningHeartbeat _heartbeat = new(leases);
    private readonly IProjectStore _projects = projects;
    private readonly DiscoverEvCapabilitiesUseCase _discover = discover;
    private readonly EvDiscoveryPolicy _policy = policy;
    private readonly IClock _clock = clock;

    /// <summary>Reivindica e processa o próximo comando de descoberta; <see langword="null"/> se não houver trabalho.</summary>
    public async Task<EvDiscoveryCommandExecution?> ProcessNextAsync(
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

            // Heartbeat PERIÓDICO real durante toda a descoberta; sua perda cancela a operação e resulta em Fenced.
            var beat = await _heartbeat.RunWhileAsync(
                lease,
                HeartbeatInterval(leaseDuration),
                token => DispatchAsync(command, fence, token),
                cancellationToken).ConfigureAwait(false);
            if (beat.Lost)
            {
                return new EvDiscoveryCommandExecution(claimed.Job.JobId, EvDiscoveryCommandOutcome.Fenced, beat.LastOutcome);
            }
        }
        catch (FencedOutException)
        {
            return new EvDiscoveryCommandExecution(claimed.Job.JobId, EvDiscoveryCommandOutcome.Fenced, JobCommandOutcome.FencedOut);
        }
        catch (Exception exception) when (IsTerminal(exception))
        {
            var failed = await _jobs.FailAsync(lease, ErrorCode.Validation, cancellationToken).ConfigureAwait(false);
            return new EvDiscoveryCommandExecution(claimed.Job.JobId, OutcomeFor(failed, EvDiscoveryCommandOutcome.Failed), failed);
        }
        catch (ConcurrencyException)
        {
            var retried = await _jobs.ScheduleRetryAsync(
                lease, ErrorCode.ConcurrencyLost, _clock.UtcNow + RetryBackoff, cancellationToken).ConfigureAwait(false);
            return new EvDiscoveryCommandExecution(claimed.Job.JobId, OutcomeFor(retried, EvDiscoveryCommandOutcome.Retried), retried);
        }
        catch (EvDiscoveryProbeException)
        {
            var retried = await _jobs.ScheduleRetryAsync(
                lease, ErrorCode.TransientProvider, _clock.UtcNow + RetryBackoff, cancellationToken).ConfigureAwait(false);
            return new EvDiscoveryCommandExecution(claimed.Job.JobId, OutcomeFor(retried, EvDiscoveryCommandOutcome.Retried), retried);
        }

        var completed = await _jobs.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
        return new EvDiscoveryCommandExecution(claimed.Job.JobId, OutcomeFor(completed, EvDiscoveryCommandOutcome.Completed), completed);
    }

    private static TimeSpan HeartbeatInterval(TimeSpan leaseDuration) =>
        leaseDuration > TimeSpan.Zero ? leaseDuration / 3.0 : TimeSpan.FromMilliseconds(1);

    // Só reporta a transição quando o comando de Job foi aplicado (ou replay idempotente). Caso contrário ⇒ Fenced.
    private static EvDiscoveryCommandOutcome OutcomeFor(JobCommandOutcome jobOutcome, EvDiscoveryCommandOutcome appliedOutcome) =>
        jobOutcome is JobCommandOutcome.Applied or JobCommandOutcome.IdempotentReplay
            ? appliedOutcome
            : EvDiscoveryCommandOutcome.Fenced;

    private async Task GuardContextAsync(EvDiscoveryCommand command, CancellationToken cancellationToken)
    {
        EvDiscoveryCommandValidation.EnsureValid(command);
        if (command.Context.DiscoveryPolicyVersion != _policy.PolicyVersion)
        {
            throw new StaleEvDiscoveryContextException(
                "Versão da política de descoberta divergente do contexto do comando (STALE_COMMAND_CONTEXT).");
        }

        var project = await _projects.GetAsync(command.Scope, cancellationToken).ConfigureAwait(false)
            ?? throw new EvDiscoveryNotFoundException("Projeto não encontrado no escopo.");
        EnsureProjectContext(command.Context, project);
    }

    private static void EnsureProjectContext(EvDiscoveryCommandContext context, MigrationProject project)
    {
        if (context.ExpectedProjectConfigurationVersion != project.ConfigurationVersion.Value)
        {
            throw new StaleEvDiscoveryContextException(
                "Versão de configuração do projeto divergente (STALE_COMMAND_CONTEXT).");
        }

        if (!string.Equals(context.ExpectedConfigurationHash?.Value, project.ConfigurationHash.Value, StringComparison.Ordinal))
        {
            throw new StaleEvDiscoveryContextException(
                "Hash de configuração do projeto divergente (STALE_COMMAND_CONTEXT).");
        }
    }

    private async Task DispatchAsync(EvDiscoveryCommand command, JobFence fence, CancellationToken cancellationToken)
    {
        var environment = new EvEnvironmentDescriptor(command.EnvironmentId, command.SiteName, command.DirectoryServer);
        // O contexto (versão/hash de configuração do projeto) já foi validado como presente por GuardContextAsync.
        await _discover.ExecuteAsync(
            command.Scope, environment, _policy,
            command.Context.ExpectedProjectConfigurationVersion!.Value, command.Context.ExpectedConfigurationHash!.Value,
            command.Correlation, cancellationToken, fence).ConfigureAwait(false);
    }

    private static bool IsTerminal(Exception exception) =>
        exception is EvDiscoveryValidationException
            or EvDiscoveryNotFoundException
            or StaleEvDiscoveryContextException
            or EvDiscoveryEvidenceException
            or EvDiscoveryPersistenceException
            or ArgumentException;
}
