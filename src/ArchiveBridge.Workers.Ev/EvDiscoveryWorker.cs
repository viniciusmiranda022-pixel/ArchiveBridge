using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Worker EV OPERACIONAL (caminho normal). A cada poll descobre os escopos elegíveis via
/// <see cref="IEvDiscoveryPendingScopeReader"/> (identidade de manutenção, somente enumeração) e, para cada
/// escopo, delega ao <see cref="EvDiscoveryCommandProcessor.ProcessNextAsync"/> — que reivindica, executa a
/// descoberta READ-ONLY e conclui/retry/fence sob a identidade normal da aplicação (RLS + projeto). Este
/// worker NÃO reimplementa regras nem executa nada inline: nunca chama PowerShell, o host Windows ou o caso
/// de uso de descoberta diretamente. Robusto: uma falha em um escopo não derruba os demais; o cancelamento é
/// propagado; não há busy-loop. Os logs são estruturados e sanitizados — nunca site, directory server,
/// payload do comando, conteúdo de evidência ou segredo.
/// </summary>
public sealed partial class EvDiscoveryWorker(
    ILogger<EvDiscoveryWorker> logger,
    EnterpriseVaultDiscoveryOptions options,
    IEvDiscoveryPendingScopeReader scopeReader,
    EvDiscoveryCommandProcessor processor) : BackgroundService
{
    // WorkerId ESTÁVEL por processo/host (não muda entre polls); base do fencing por owner.
    private readonly WorkerId _workerId = new($"ev-discovery-{Environment.MachineName}-{Environment.ProcessId}");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, _workerId.Value, options.MaxScopesPerPoll, options.PollIntervalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento cooperativo (cancelamento propagado); nada a fazer.
        }

        LogStopped(logger, _workerId.Value);
    }

    // Um poll: enumera escopos (correlação própria) e processa um comando por escopo. A enumeração pode falhar
    // de forma transitória (banco indisponível): loga sanitizado e tenta de novo no próximo poll.
    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        var correlation = CorrelationId.New();
        IReadOnlyList<TenantScope> scopes;
        try
        {
            scopes = await scopeReader.ListEligibleScopesAsync(options.MaxScopesPerPoll, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Cancelamento propaga (encerramento); falha transitória é sanitizada e reeavaliada no próximo poll.
            LogScopeEnumerationFailed(logger, correlation.Value, exception.GetType().Name);
            return;
        }

        LogScopesDiscovered(logger, scopes.Count, correlation.Value);

        foreach (var scope in scopes)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessScopeAsync(scope, correlation, stoppingToken).ConfigureAwait(false);
        }
    }

    // Processa UM comando do escopo pelo processor da Application. Uma exceção (não-cancelamento) é isolada
    // ao escopo — loga o código de erro sanitizado (tipo, sem mensagem) e segue para os demais escopos.
    private async Task ProcessScopeAsync(TenantScope scope, CorrelationId correlation, CancellationToken stoppingToken)
    {
        try
        {
            var execution = await processor
                .ProcessNextAsync(scope, _workerId, options.LeaseDuration, correlation, stoppingToken)
                .ConfigureAwait(false);
            if (execution is not null)
            {
                LogOutcome(logger, execution.Job.Value, execution.Outcome, correlation.Value);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Cancelamento propaga; qualquer outra falha fica ISOLADA a este escopo (não derruba os demais).
            LogScopeFailed(logger, correlation.Value, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker EV operacional iniciado: worker {WorkerId}, até {MaxScopes} escopos/poll a cada {PollSeconds}s.")]
    private static partial void LogStarted(ILogger logger, string workerId, int maxScopes, int pollSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV operacional encerrado: worker {WorkerId}.")]
    private static partial void LogStopped(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker EV: {ScopeCount} escopo(s) elegível(is) no poll {Correlation}.")]
    private static partial void LogScopesDiscovered(ILogger logger, int scopeCount, Guid correlation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Worker EV: falha ao enumerar escopos no poll {Correlation} (código {ErrorCode}).")]
    private static partial void LogScopeEnumerationFailed(ILogger logger, Guid correlation, string errorCode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker EV: Job {JobId} desfecho {Outcome} no poll {Correlation}.")]
    private static partial void LogOutcome(ILogger logger, Guid jobId, EvDiscoveryCommandOutcome outcome, Guid correlation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Worker EV: falha isolada ao processar um escopo no poll {Correlation} (código {ErrorCode}).")]
    private static partial void LogScopeFailed(ILogger logger, Guid correlation, string errorCode);
}
