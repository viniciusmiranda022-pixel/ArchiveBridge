using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Ferramenta de DIAGNÓSTICO/desenvolvimento (NÃO é o caminho operacional). Executa um ciclo sintético do
/// backbone durável de Jobs (cria → reivindica → simula → conclui um Job de demonstração) e só roda quando
/// <c>SyntheticJobMode:Enabled=true</c> — o padrão é <b>false</b>. É explicitamente BLOQUEADA em Production e
/// isolada do worker operacional real (<see cref="EvDiscoveryWorker"/>): nunca interfere no fluxo normal.
/// Requer as identidades <c>ConnectionStrings:JobStoreApp</c> e <c>ConnectionStrings:JobStoreMaintenance</c>.
/// Nunca chama o Enterprise Vault, nunca gera PST e não declara capacidade de exportação.
/// </summary>
public sealed partial class SyntheticEvJobWorker(
    ILogger<SyntheticEvJobWorker> logger,
    IConfiguration configuration,
    IHostEnvironment environment) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    // agingInterval adotado: 30s. Limite máximo de espera derivado =
    // (JobPriority.MaxValue - JobPriority.MinValue) * agingInterval = 100 * 30s = 3000s = 50 min.
    private static readonly TimeSpan AgingInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("SyntheticJobMode:Enabled", defaultValue: false))
        {
            LogSyntheticDisabled(logger);
            return;
        }

        if (environment.IsProduction())
        {
            LogSyntheticBlockedInProduction(logger);
            return;
        }

        var appConnectionString = configuration.GetConnectionString("JobStoreApp");
        var maintenanceConnectionString = configuration.GetConnectionString("JobStoreMaintenance");
        if (string.IsNullOrWhiteSpace(appConnectionString) || string.IsNullOrWhiteSpace(maintenanceConnectionString))
        {
            LogMissingConnections(logger);
            return;
        }

        var clock = new SystemClock();
        var factory = new TenantConnectionFactory(appConnectionString, maintenanceConnectionString);
        var store = new SqlJobStore(factory, clock, AgingInterval);

        var scope = new TenantScope(new TenantId(Guid.NewGuid()), new ProjectId(Guid.NewGuid()));
        var worker = new WorkerId($"ev-synthetic-{Environment.MachineName}");
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

        // Execução simulada e controlada — SEM Enterprise Vault, SEM gerar/ler PST.
        LogSimulating(logger, claimed.JobId.Value);

        var outcome = await store.CompleteAsync(
            new LeaseCommand(scope, claimed.JobId, worker, claimed.Epoch, correlation), stoppingToken);
        LogCompleted(logger, claimed.JobId.Value, outcome);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker EV sintético ocioso: SyntheticJobMode desabilitado (nenhum dado sintético criado).")]
    private static partial void LogSyntheticDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Worker EV sintético: habilitado, mas o ambiente é Production — ciclo BLOQUEADO.")]
    private static partial void LogSyntheticBlockedInProduction(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Worker EV sintético: habilitado, mas faltam ConnectionStrings:JobStoreApp/JobStoreMaintenance.")]
    private static partial void LogMissingConnections(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV sintético: Job sintético criado {JobId}.")]
    private static partial void LogCreated(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV sintético: nada elegível para reivindicar.")]
    private static partial void LogNothingClaimed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV sintético: Job {JobId} reivindicado na época {Epoch}.")]
    private static partial void LogClaimed(ILogger logger, Guid jobId, long epoch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV sintético: execução simulada do Job {JobId} (sem EV/PST).")]
    private static partial void LogSimulating(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker EV sintético: Job {JobId} concluído com desfecho {Outcome}.")]
    private static partial void LogCompleted(ILogger logger, Guid jobId, JobCommandOutcome outcome);
}
