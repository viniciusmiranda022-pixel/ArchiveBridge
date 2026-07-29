using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.Extensions.Configuration;

namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Primeiro consumidor técnico do ciclo durável de Jobs (Vertical Slice 1). Quando há uma conexão
/// <c>ConnectionStrings:JobStore</c> configurada, executa UM ciclo sintético e controlado:
/// cria um Job → reivindica com lease → renova o lease → simula a execução → conclui. Sem conexão,
/// permanece ocioso. NÃO chama o Enterprise Vault, NÃO gera PST e NÃO declara capacidade de
/// exportação — apenas exercita a fila durável, os leases e o fencing.
/// </summary>
public sealed partial class EvWorker(ILogger<EvWorker> logger, IConfiguration configuration) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("JobStore");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LogNoConnection(logger);
            return;
        }

        await new MigrationRunner(connectionString).ApplyAsync(stoppingToken);

        var clock = new SystemClock();
        var factory = new TenantConnectionFactory(connectionString);
        var store = new SqlJobStore(factory, clock);
        var leases = new SqlJobLeaseManager(factory, clock, RetryPolicy.Default, LeaseDuration);

        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var worker = new WorkerId($"ev-worker-{Environment.MachineName}");
        var correlation = Domain.Common.CorrelationId.New();

        var jobId = await store.CreateAsync(
            new CreateJobCommand(scope, Workload.EnterpriseVault, JobPriority.Normal, correlation), stoppingToken);
        LogCreated(logger, jobId.Value);

        var claimed = await store.TryClaimNextAsync(
            new ClaimRequest(scope, Workload.EnterpriseVault, worker, LeaseDuration, correlation), stoppingToken);
        if (claimed is null)
        {
            LogNothingClaimed(logger);
            return;
        }

        LogClaimed(logger, claimed.JobId.Value, claimed.Epoch.Value);

        await leases.RenewAsync(
            new LeaseCommand(scope, claimed.JobId, worker, claimed.Epoch, correlation), stoppingToken);

        // Execução simulada e controlada — SEM Enterprise Vault, SEM gerar/ler PST.
        LogSimulating(logger, claimed.JobId.Value);

        var outcome = await store.CompleteAsync(
            new LeaseCommand(scope, claimed.JobId, worker, claimed.Epoch, correlation), stoppingToken);
        LogCompleted(logger, claimed.JobId.Value, outcome);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker EV ocioso: ConnectionStrings:JobStore não configurada (nenhum ciclo executado).")]
    private static partial void LogNoConnection(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV: Job sintético criado {JobId}.")]
    private static partial void LogCreated(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV: nada elegível para reivindicar.")]
    private static partial void LogNothingClaimed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV: Job {JobId} reivindicado na época {Epoch}.")]
    private static partial void LogClaimed(ILogger logger, Guid jobId, long epoch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV: execução simulada do Job {JobId} (sem EV/PST).")]
    private static partial void LogSimulating(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV: Job {JobId} concluído com desfecho {Outcome}.")]
    private static partial void LogCompleted(ILogger logger, Guid jobId, JobCommandOutcome outcome);
}
