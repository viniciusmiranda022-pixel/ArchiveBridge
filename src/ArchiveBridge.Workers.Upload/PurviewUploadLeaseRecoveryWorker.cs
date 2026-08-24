using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Upload;

/// <summary>
/// Reaper de CRASH RECOVERY do workload Upload. A cada intervalo, recupera os Jobs cujo lease expirou após
/// uma queda de worker (Processing + <c>lease_expires_at &lt; agora</c>) e os devolve a RetryScheduled (ou
/// Failed, se as tentativas se esgotaram) — de modo que uma queda de worker NUNCA perca o Job. É RESTRITO
/// ao workload Upload: nunca toca Jobs de outros workloads. Responsabilidade ÚNICA — não enumera escopos de
/// upload, não chama o processor, não invoca AzCopy. Robusto: uma falha transitória de banco é sanitizada e
/// reavaliada no próximo ciclo. Logs estruturados e sanitizados — nunca wave, SAS ou evidência.
/// </summary>
public sealed partial class PurviewUploadLeaseRecoveryWorker(
    ILogger<PurviewUploadLeaseRecoveryWorker> logger,
    PurviewUploadWorkerOptions options,
    IJobLeaseManager leaseManager) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.LeaseRecoveryBatchSize, options.LeaseRecoveryIntervalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.LeaseRecoveryInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento cooperativo (cancelamento propagado); nada a fazer.
        }

        LogStopped(logger);
    }

    private async Task RecoverOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await leaseManager
                .RecoverExpiredLeasesAsync(Workload.Upload, options.LeaseRecoveryBatchSize, stoppingToken)
                .ConfigureAwait(false);
            if (recovered > 0)
            {
                LogRecovered(logger, recovered);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecoveryFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reaper Upload iniciado: recupera até {BatchSize} lease(s) expirado(s) a cada {IntervalSeconds}s (workload Upload).")]
    private static partial void LogStarted(ILogger logger, int batchSize, int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reaper Upload encerrado.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reaper Upload: {Recovered} lease(s) expirado(s) recuperado(s) neste ciclo.")]
    private static partial void LogRecovered(ILogger logger, int recovered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaper Upload: falha transitória no ciclo (código {ErrorCode}); nova tentativa no próximo intervalo.")]
    private static partial void LogRecoveryFailed(ILogger logger, string errorCode);
}
