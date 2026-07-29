namespace ArchiveBridge.Workers.Reconciliation;

/// <summary>
/// SCAFFOLDING NÃO FUNCIONAL. Worker isolado (ADR-0001) para reconciliação esperado × observado (§26.3).
/// Placeholder: registra a partida e permanece ocioso — NÃO reivindica a fila, NÃO acessa o
/// Enterprise Vault, o PST, o destino M365 nem qualquer serviço externo. A lógica real entra
/// por slice posterior. Não representa capacidade funcional de migração.
/// </summary>
public sealed partial class ReconciliationWorker(ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Worker Reconciliation (scaffolding) iniciado; sem processamento real.")]
    private static partial void LogStarted(ILogger logger);
}
