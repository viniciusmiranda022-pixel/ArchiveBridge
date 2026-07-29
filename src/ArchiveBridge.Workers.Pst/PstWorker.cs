namespace ArchiveBridge.Workers.Pst;

/// <summary>
/// SCAFFOLDING NÃO FUNCIONAL. Worker isolado (ADR-0001) para processamento e validação independente de PST (§18).
/// Placeholder: registra a partida e permanece ocioso — NÃO reivindica a fila, NÃO acessa o
/// Enterprise Vault, o PST, o destino M365 nem qualquer serviço externo. A lógica real entra
/// por slice posterior. Não representa capacidade funcional de migração.
/// </summary>
public sealed partial class PstWorker(ILogger<PstWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Worker Pst (scaffolding) iniciado; sem processamento real.")]
    private static partial void LogStarted(ILogger logger);
}
