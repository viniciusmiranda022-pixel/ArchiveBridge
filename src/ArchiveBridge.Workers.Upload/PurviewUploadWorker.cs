using ArchiveBridge.Application.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Upload;

/// <summary>
/// Worker de upload Purview OPERACIONAL. A cada poll descobre os escopos elegíveis via
/// <see cref="IPurviewUploadPendingScopeReader"/> (identidade de manutenção, somente enumeração) e, para
/// cada escopo, delega ao <see cref="PurviewUploadCommandProcessor.ProcessNextAsync"/> — que reivindica,
/// resolve server-side, revalida fisicamente, adquire o SAS, transporta e conclui/retry/fence sob a
/// identidade normal da aplicação. Este worker NÃO reimplementa regras nem executa nada inline. Robusto:
/// uma falha em um escopo não derruba os demais. Logs estruturados e sanitizados — nunca wave, SAS, binding
/// ou evidência bruta.
/// </summary>
public sealed partial class PurviewUploadWorker(
    ILogger<PurviewUploadWorker> logger,
    PurviewUploadWorkerOptions options,
    IPurviewUploadPendingScopeReader scopeReader,
    PurviewUploadCommandProcessor processor) : BackgroundService
{
    private readonly WorkerId _workerId = new($"purview-upload-{Environment.MachineName}-{Environment.ProcessId}");

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
            LogScopeFailed(logger, correlation.Value, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker de upload Purview iniciado: worker {WorkerId}, até {MaxScopes} escopos/poll a cada {PollSeconds}s.")]
    private static partial void LogStarted(ILogger logger, string workerId, int maxScopes, int pollSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker de upload Purview encerrado: worker {WorkerId}.")]
    private static partial void LogStopped(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Worker de upload Purview: {ScopeCount} escopo(s) elegível(is) no poll {Correlation}.")]
    private static partial void LogScopesDiscovered(ILogger logger, int scopeCount, Guid correlation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Worker de upload Purview: falha ao enumerar escopos no poll {Correlation} (código {ErrorCode}).")]
    private static partial void LogScopeEnumerationFailed(ILogger logger, Guid correlation, string errorCode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker de upload Purview: Job {JobId} desfecho {Outcome} no poll {Correlation}.")]
    private static partial void LogOutcome(ILogger logger, Guid jobId, PurviewUploadCommandOutcome outcome, Guid correlation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Worker de upload Purview: falha isolada ao processar um escopo no poll {Correlation} (código {ErrorCode}).")]
    private static partial void LogScopeFailed(ILogger logger, Guid correlation, string errorCode);
}
