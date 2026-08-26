using ArchiveBridge.Application.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Export;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Export;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.EnterpriseVault.Connector;
using ArchiveBridge.Infrastructure.EnterpriseVault.Export;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Ev;

/// <summary>
/// Raiz de composição do Worker de exportação EV (AB-4C-005, Slice 4C Passo 2). Mesmo desenho fail-closed
/// de <see cref="EnterpriseVaultDiscoveryComposition"/>: com <c>Enabled=false</c> nenhum efeito de
/// exportação é registrado; com <c>Enabled=true</c>, configuração inválida, raiz UNC não allowlisted, ou um
/// host incapaz de executar o exporter real (não-Windows) derrubam o host ANTES de qualquer efeito.
/// </summary>
public static class EnterpriseVaultExportComposition
{
    private static readonly TimeSpan AgingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Compõe o worker de exportação operacional (quando habilitado).</summary>
    public static void Configure(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(EnterpriseVaultExportOptions.SectionName)
            .Get<EnterpriseVaultExportOptions>() ?? new EnterpriseVaultExportOptions();
        builder.Services.AddSingleton(options);

        if (!options.Enabled)
        {
            return; // Enabled=false ⇒ o worker de exportação NÃO é registrado (não executa).
        }

        var applicationConnection = builder.Configuration.GetConnectionString("Application");
        var maintenanceConnection = builder.Configuration.GetConnectionString("Maintenance");

        options.ValidateForOperation(applicationConnection, maintenanceConnection);
        var outputRootOptions = new EvExportOutputRootOptions(options.OutputRoot, options.AllowedUncRoots);
        outputRootOptions.Validate(); // fail-closed: raiz UNC não allowlisted derruba o startup.
        EnterpriseVaultDiscoveryComposition.EnsureRealEvHostSupported(OperatingSystem.IsWindows());

        RegisterOperationalServices(builder.Services, options, applicationConnection!, maintenanceConnection!);
        builder.Services.AddHostedService<EvExportWorker>();
    }

    private static void RegisterOperationalServices(
        IServiceCollection services, EnterpriseVaultExportOptions options, string applicationConnection, string maintenanceConnection)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? AppContext.BaseDirectory
            : options.WorkingDirectory;

        IClock clock = new SystemClock();
        var factory = new TenantConnectionFactory(applicationConnection, maintenanceConnection);

        var scopeReader = new SqlEvExportPendingScopeReader(factory, clock);

        var inbox = new SqlEvExportCommandInbox(factory, clock);
        var jobStore = new SqlJobStore(factory, clock, AgingInterval);
        var leaseManager = new SqlJobLeaseManager(factory, clock, RetryPolicy.Default, options.LeaseDuration);
        var connectors = new SqlConnectorRegistry(factory);
        var capabilities = new SqlConnectorCapabilityStore(factory);
        var throttle = new SqlEvExportThrottleLeaseStore(factory, clock);
        var attempts = new SqlEvExportAttemptStore(factory, clock);
        var audit = new SqlEvExportAuditTrail(factory);
        var inspector = new FileSystemEvExportOutputInspector();
        var executor = new WindowsEvArchiveExportExecutor(options.PowerShellExecutable, workingDirectory);

        var exportPolicy = EvExportPolicy.Create(
            EvExportPolicy.CurrentVersion, ExportRequestPolicy.MaxThreadsCeiling, ExportRequestPolicy.MaxPstSizeMbCeiling,
            options.ProcessTimeout, maxOutputBytes: 64L * 1024);

        var processor = new EvExportCommandProcessor(
            inbox, jobStore, leaseManager, connectors, capabilities, throttle, executor, inspector, attempts, audit,
            exportPolicy, options.OutputRoot, clock, RetryPolicy.Default);

        services.AddSingleton<IEvExportPendingScopeReader>(scopeReader);
        services.AddSingleton(processor);
        services.AddSingleton<IJobLeaseManager>(leaseManager);
    }
}
