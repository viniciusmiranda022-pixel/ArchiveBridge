using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Reaper de CRASH RECOVERY do workload EnterpriseVault. A cada intervalo, recupera os Jobs cujo lease
/// expirou após uma queda de worker (Processing + <c>lease_expires_at &lt; agora</c>) e os devolve a
/// RetryScheduled (ou Failed, se as tentativas se esgotaram) — de modo que uma queda de worker NUNCA perca
/// o Job. É RESTRITO ao workload EnterpriseVault: nunca toca Jobs de outros workloads (isolamento entre
/// workers). Responsabilidade ÚNICA — não enumera escopos de descoberta, não chama o processor, não executa
/// discovery, não chama PowerShell nem o Enterprise Vault. Robusto: uma falha transitória de banco é
/// sanitizada e reavaliada no próximo ciclo (não derruba o processo); o cancelamento é cooperativo. Logs
/// estruturados e sanitizados — nunca tenant, projeto, site, directory server, comando, evidência ou segredo.
/// </summary>
public sealed partial class EvLeaseRecoveryWorker(
    ILogger<EvLeaseRecoveryWorker> logger,
    EnterpriseVaultDiscoveryOptions options,
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

    // Um ciclo: recupera até LeaseRecoveryBatchSize leases EXPIRADOS do workload EV. Uma falha transitória de
    // banco não derruba o processo operacional — é sanitizada e reavaliada no próximo ciclo.
    private async Task RecoverOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await leaseManager
                .RecoverExpiredLeasesAsync(Workload.EnterpriseVault, options.LeaseRecoveryBatchSize, stoppingToken)
                .ConfigureAwait(false);
            if (recovered > 0)
            {
                LogRecovered(logger, recovered);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Cancelamento propaga (encerramento); qualquer outra falha é sanitizada (tipo, sem mensagem).
            LogRecoveryFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reaper EV iniciado: recupera até {BatchSize} lease(s) expirado(s) a cada {IntervalSeconds}s (workload EnterpriseVault).")]
    private static partial void LogStarted(ILogger logger, int batchSize, int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reaper EV encerrado.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reaper EV: {Recovered} lease(s) expirado(s) recuperado(s) neste ciclo.")]
    private static partial void LogRecovered(ILogger logger, int recovered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaper EV: falha transitória no ciclo (código {ErrorCode}); nova tentativa no próximo intervalo.")]
    private static partial void LogRecoveryFailed(ILogger logger, string errorCode);
}
