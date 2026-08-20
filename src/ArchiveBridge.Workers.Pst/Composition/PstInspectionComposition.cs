using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Pst.Composition;

/// <summary>
/// Raiz de composição da capacidade de inspeção PST (Slice 4B, Passo 1). Reutiliza as implementações
/// REAIS existentes (sem versões paralelas) e é FAIL-CLOSED no startup: com <c>Enabled=false</c> nenhum
/// serviço é registrado (nenhuma conexão SQL é sequer aberta); com <c>Enabled=true</c>, configuração
/// inválida derruba o host antes de qualquer efeito. Este Passo registra apenas o caso de uso
/// diretamente invocável (<see cref="InspectPstArtifactUseCase"/>) — NÃO registra um worker que reivindica
/// fila (nenhuma orquestração assíncrona foi autorizada neste Passo; ver STOP-THE-LINE do work order).
/// </summary>
public static class PstInspectionComposition
{
    /// <summary>Compõe o host: opções tipadas e os serviços de inspeção (quando habilitados).</summary>
    public static void Configure(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(PstInspectionOptions.SectionName).Get<PstInspectionOptions>()
            ?? new PstInspectionOptions();
        builder.Services.AddSingleton(options);

        if (!options.Enabled)
        {
            return; // Enabled=false ⇒ nenhum serviço de inspeção é registrado (não executa, não conecta).
        }

        var applicationConnection = builder.Configuration.GetConnectionString("Application");
        var maintenanceConnection = builder.Configuration.GetConnectionString("Maintenance");

        // Fail-closed no startup: configuração inválida derruba o host antes de qualquer conexão.
        options.ValidateForOperation(applicationConnection, maintenanceConnection);

        RegisterServices(builder.Services, options, applicationConnection!, maintenanceConnection!);
    }

    private static void RegisterServices(
        IServiceCollection services, PstInspectionOptions options, string applicationConnection, string maintenanceConnection)
    {
        var connectionFactory = new TenantConnectionFactory(applicationConnection, maintenanceConnection);
        var clock = new SystemClock();
        var storageOptions = new PstStorageOptions
        {
            RootPath = options.RootPath,
            MaxSizeBytes = options.MaxSizeBytes,
            Timeout = options.Timeout,
        };

        var custodyStore = new SqlPstCustodyStore(connectionFactory, clock);
        IPstInspectionStore inspectionStore = new SqlPstInspectionStore(connectionFactory);
        IPstEngine engine = new HeaderOnlyPstInspectionEngine(custodyStore, storageOptions);

        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IPstCustodyStore>(custodyStore);
        services.AddSingleton(inspectionStore);
        services.AddSingleton(engine);
        services.AddSingleton(new InspectPstArtifactUseCase(custodyStore, inspectionStore, engine, clock));
    }
}
