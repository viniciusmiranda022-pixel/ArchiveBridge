namespace ArchiveBridge.Workers.Upload;

/// <summary>
/// SCAFFOLDING NÃO FUNCIONAL. Worker isolado (ADR-0001) para ingestão no destino Microsoft 365 (ADR-0006).
/// Placeholder: registra a partida e permanece ocioso — NÃO reivindica a fila, NÃO acessa o
/// Enterprise Vault, o PST, o destino M365 nem qualquer serviço externo. A lógica real entra
/// por slice posterior. Não representa capacidade funcional de migração.
/// </summary>
public sealed partial class UploadWorker(ILogger<UploadWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Worker Upload (scaffolding) iniciado; sem processamento real.")]
    private static partial void LogStarted(ILogger logger);
}
