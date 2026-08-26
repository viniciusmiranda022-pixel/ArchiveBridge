using ArchiveBridge.Application.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.Projects;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Raiz de composição do host do Worker EV. Reutiliza as implementações REAIS existentes (sem versões
/// paralelas) e é FAIL-CLOSED no startup: com <c>Enabled=false</c> o worker operacional não é registrado;
/// com <c>Enabled=true</c>, configuração inválida ou um host incapaz de executar o Enterprise Vault real
/// (não-Windows) derrubam o host antes de qualquer efeito. O modo sintético é isolado e só é registrado sob
/// habilitação explícita.
/// </summary>
public static class EnterpriseVaultDiscoveryComposition
{
    // agingInterval do SqlJobStore (usado apenas em seu próprio claim, que o processor EV não exerce; a fila
    // EV reivindica pelo SqlEvDiscoveryCommandInbox). Qualquer valor positivo serve; 30s por convenção.
    private static readonly TimeSpan AgingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Compõe o host: opções tipadas, worker operacional (quando habilitado) e worker sintético (diagnóstico).</summary>
    public static void Configure(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(EnterpriseVaultDiscoveryOptions.SectionName)
            .Get<EnterpriseVaultDiscoveryOptions>() ?? new EnterpriseVaultDiscoveryOptions();
        builder.Services.AddSingleton(options);

        // Diagnóstico/dev isolado: default false, e a própria classe bloqueia Production.
        if (builder.Configuration.GetValue("SyntheticJobMode:Enabled", defaultValue: false))
        {
            builder.Services.AddHostedService<SyntheticEvJobWorker>();
        }

        if (!options.Enabled)
        {
            return; // Enabled=false ⇒ o worker operacional NÃO é registrado (não executa).
        }

        var applicationConnection = builder.Configuration.GetConnectionString("Application");
        var maintenanceConnection = builder.Configuration.GetConnectionString("Maintenance");

        // Fail-closed no startup: configuração inválida OU host não-Windows lançam antes de o host subir.
        options.ValidateForOperation(applicationConnection, maintenanceConnection);
        EnsureRealEvHostSupported(OperatingSystem.IsWindows());

        RegisterOperationalServices(builder.Services, options, applicationConnection!, maintenanceConnection!);
        builder.Services.AddHostedService<EvDiscoveryWorker>();
        // Reaper de crash recovery (workload EV): recupera Jobs cujo lease expirou após uma queda de worker.
        builder.Services.AddHostedService<EvLeaseRecoveryWorker>();
    }

    /// <summary>
    /// O Enterprise Vault operacional real só é suportado em um Windows Worker. Fora dele, fail-closed
    /// explícito — nunca produzir "Ready" com sonda falsa em runtime de produção.
    /// </summary>
    public static void EnsureRealEvHostSupported(bool isWindowsHost)
    {
        if (!isWindowsHost)
        {
            throw new PlatformNotSupportedException(
                "EnterpriseVaultDiscovery:Enabled=true requer um Windows Worker com a integração Enterprise Vault; " +
                "o host atual não pode executá-la (fail-closed). A prova contra EV real é pendente de laboratório.");
        }
    }

    // Compõe o grafo REAL do processor + o leitor de escopos, reutilizando as implementações existentes.
    private static void RegisterOperationalServices(
        IServiceCollection services,
        EnterpriseVaultDiscoveryOptions options,
        string applicationConnection,
        string maintenanceConnection)
    {
        var evidenceRoot = Path.IsPathRooted(options.EvidenceRoot)
            ? options.EvidenceRoot
            : Path.Combine(AppContext.BaseDirectory, options.EvidenceRoot);
        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? AppContext.BaseDirectory
            : options.WorkingDirectory;

        IClock clock = new SystemClock();
        var factory = new TenantConnectionFactory(applicationConnection, maintenanceConnection);

        // Enumeração de escopos (identidade de manutenção; somente leitura).
        var scopeReader = new SqlEvDiscoveryPendingScopeReader(factory, clock);

        // Pipeline de processamento (identidade da aplicação; RLS + projeto). Reutiliza as implementações reais.
        var inbox = new SqlEvDiscoveryCommandInbox(factory, clock);
        var jobStore = new SqlJobStore(factory, clock, AgingInterval);
        var leaseManager = new SqlJobLeaseManager(factory, clock, RetryPolicy.Default, options.LeaseDuration);
        var projectStore = new SqlProjectStore(factory, clock);
        var discoveryStore = new SqlEvDiscoveryStore(factory, clock);
        var evidenceStore = new FileSystemEvDiscoveryEvidenceStore(evidenceRoot);
        var serializer = new EvDiscoveryEvidenceSerializer();
        var adapters = new AdapterCompatibilityEvaluator([new EvExportDocumented151Adapter()]);
        var powerShellHost = new WindowsEvPowerShellHost(options.PowerShellExecutable, workingDirectory);
        var discovery = new PowerShellEvCapabilityDiscovery(powerShellHost, clock);
        var discoverUseCase = new DiscoverEvCapabilitiesUseCase(
            discovery, adapters, serializer, discoveryStore, evidenceStore, clock);
        var processor = new EvDiscoveryCommandProcessor(
            inbox, jobStore, leaseManager, projectStore, discoverUseCase, EvDiscoveryPolicy.Default, clock, RetryPolicy.Default);

        services.AddSingleton<IEvDiscoveryPendingScopeReader>(scopeReader);
        services.AddSingleton(processor);
        // MESMA instância do lease manager do processor: o reaper e o processamento compartilham a política
        // e a duração de lease (não há SqlJobLeaseManager duplicado com políticas divergentes).
        services.AddSingleton<IJobLeaseManager>(leaseManager);
    }
}
