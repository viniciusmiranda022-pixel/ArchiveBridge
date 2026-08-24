using System.Runtime.Versioning;
using ArchiveBridge.Application.TargetIngestion.Purview;
using ArchiveBridge.Application.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;
using ArchiveBridge.Infrastructure.Time;
using ArchiveBridge.Infrastructure.Waves;
using ArchiveBridge.Infrastructure.WavePartitionBindings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Upload;

/// <summary>
/// Raiz de composição do Worker de upload Purview operacional (AB-I5-009). Mesmo desenho fail-closed de
/// <c>EnterpriseVaultExportComposition</c>: com <c>Enabled=false</c> nenhum efeito de upload é registrado;
/// com <c>Enabled=true</c>, configuração inválida ou um host incapaz de executar a custódia DPAPI real do
/// SAS (não-Windows, ADR-0008) derrubam o host ANTES de qualquer efeito.
/// </summary>
public static class PurviewUploadComposition
{
    /// <summary>Compõe o worker de upload operacional (quando habilitado).</summary>
    public static void Configure(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(PurviewUploadWorkerOptions.SectionName)
            .Get<PurviewUploadWorkerOptions>() ?? new PurviewUploadWorkerOptions();
        builder.Services.AddSingleton(options);

        if (!options.Enabled)
        {
            return; // Enabled=false ⇒ o worker de upload NÃO é registrado (não executa).
        }

        var applicationConnection = builder.Configuration.GetConnectionString("Application");
        var maintenanceConnection = builder.Configuration.GetConnectionString("Maintenance");

        options.ValidateForOperation(applicationConnection, maintenanceConnection);
        EnsureRealUploadHostSupported(OperatingSystem.IsWindows());

        // Suprime CA1416: EnsureRealUploadHostSupported acima já lançou PlatformNotSupportedException
        // fail-closed para qualquer host não-Windows — o analisador de compatibilidade de plataforma não
        // rastreia esse guard através da fronteira do método, mas a segurança em runtime já foi provada.
#pragma warning disable CA1416
        RegisterOperationalServices(builder.Services, options, applicationConnection!, maintenanceConnection!);
#pragma warning restore CA1416
        builder.Services.AddHostedService<PurviewUploadWorker>();
        // Reaper de crash recovery (workload Upload): recupera Jobs cujo lease expirou após uma queda de worker.
        builder.Services.AddHostedService<PurviewUploadLeaseRecoveryWorker>();
    }

    /// <summary>
    /// A custódia DPAPI real do SAS (ADR-0008, Passo 2) só é suportada em um Windows Worker. Fora dele,
    /// fail-closed explícito — nunca produzir "Ready" com um secret store incapaz de operar em runtime real.
    /// </summary>
    public static void EnsureRealUploadHostSupported(bool isWindowsHost)
    {
        if (!isWindowsHost)
        {
            throw new PlatformNotSupportedException(
                "PurviewUpload:Enabled=true requer um Windows Worker (custódia DPAPI do SAS, ADR-0008); " +
                "o host atual não pode executá-lo (fail-closed).");
        }
    }

    // Verdadeiramente Windows-only: constrói DpapiSecretStore (ADR-0008). Só é chamado depois que
    // EnsureRealUploadHostSupported já provou, em runtime, que o host é Windows (ver Configure acima).
    [SupportedOSPlatform("windows")]
    private static void RegisterOperationalServices(
        IServiceCollection services, PurviewUploadWorkerOptions options, string applicationConnection, string maintenanceConnection)
    {
        IClock clock = new SystemClock();
        var factory = new TenantConnectionFactory(applicationConnection, maintenanceConnection);

        var jobStore = new SqlJobStore(factory, clock, agingInterval: TimeSpan.FromSeconds(30));
        var leaseManager = new SqlJobLeaseManager(factory, clock, RetryPolicy.Default, options.LeaseDuration);
        var uploadRequests = new SqlPurviewUploadRequestStore(factory, clock);
        var waves = new SqlWaveStore(factory, clock);
        var bindings = new SqlWavePartitionOutputBindingStore(factory);
        var executions = new SqlPartitionExecutionStore(factory);
        var sasHandles = new SqlPurviewSasUploadHandleStore(factory, clock);
        var secretStore = new DpapiSecretStore(factory, clock);
        var sasAcquisition = new AcquireSasForUploadUseCase(sasHandles, secretStore, clock, options.SasClaimLeaseDuration);
        var attempts = new SqlPurviewUploadAttemptStore(factory);
        var scopeReader = new SqlPurviewUploadPendingScopeReader(factory, clock);

        var outputOptions = new PartitionExecutionOutputOptions { RootPath = options.PartitionOutputRoot };
        var verifier = new LocalSinglePartExecutionVerifier(outputOptions);

        var azCopyOptions = new AzCopyWorkerOptions
        {
            ExecutablePath = options.AzCopyExecutablePath,
            DeclaredVersion = options.AzCopyDeclaredVersion,
            HomologatedSha256Hexes = options.AzCopyHomologatedSha256Hexes,
            LogRoot = options.AzCopyLogRoot,
        };
        var azcopy = new AzCopyUploadProcessExecutor(azCopyOptions, outputOptions);
        var homologatedCatalog = new AzCopyHomologationCatalog(
            [.. options.AzCopyHomologatedSha256Hexes.Select(hash => new AzCopyBinaryIdentity(options.AzCopyDeclaredVersion, new(hash)))]);

        var processor = new PurviewUploadCommandProcessor(
            jobStore, leaseManager, uploadRequests, waves, bindings, executions, verifier, sasHandles, sasAcquisition,
            azcopy, attempts, homologatedCatalog, options.AzCopyProcessTimeout, clock);

        var requestUseCase = new RequestPurviewUploadUseCase(waves, uploadRequests);

        services.AddSingleton<IPurviewUploadPendingScopeReader>(scopeReader);
        services.AddSingleton(processor);
        services.AddSingleton(requestUseCase);
        services.AddSingleton<IJobLeaseManager>(leaseManager);
    }
}
