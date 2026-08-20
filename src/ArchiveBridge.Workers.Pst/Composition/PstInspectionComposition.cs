using ArchiveBridge.Application.PstProcessing;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Persistence;
using ArchiveBridge.Infrastructure.PstProcessing;
using ArchiveBridge.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArchiveBridge.Workers.Pst.Composition;

/// <summary>
/// Raiz de composição das capacidades PST do Slice 4B: inspeção (Passo 1) e PLANEJAMENTO de particionamento
/// (Passo 2). Reutiliza as implementações REAIS existentes (sem versões paralelas) e é FAIL-CLOSED no
/// startup: com <c>Enabled=false</c> nenhum serviço é registrado (nenhuma conexão SQL é sequer aberta); com
/// <c>Enabled=true</c>, configuração inválida derruba o host antes de qualquer efeito. O planejamento tem
/// sua própria chave (<see cref="PstPartitionPlanningOptions"/>, também <c>false</c> por padrão) e exige a
/// inspeção habilitada — habilitá-lo sozinho derruba o host em vez de ser ignorado silenciosamente.
/// Registra apenas casos de uso diretamente invocáveis (<see cref="InspectPstArtifactUseCase"/>,
/// <see cref="PlanPstPartitionUseCase"/>) — NÃO registra worker que reivindica fila (nenhuma orquestração
/// assíncrona foi autorizada nestes Passos; ver STOP-THE-LINE dos work orders).
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

        var planningOptions = builder.Configuration.GetSection(PstPartitionPlanningOptions.SectionName)
            .Get<PstPartitionPlanningOptions>() ?? new PstPartitionPlanningOptions();
        builder.Services.AddSingleton(planningOptions);

        if (!options.Enabled)
        {
            // Inspeção desabilitada ⇒ nenhum serviço é registrado (não executa, não conecta). Se o
            // planejamento estivesse habilitado sozinho, isso seria uma configuração incoerente e SILENCIOSA:
            // valida aqui para derrubar o host em vez de ignorar a intenção do operador.
            if (planningOptions.Enabled)
            {
                planningOptions.ValidateForOperation(inspectionEnabled: false);
            }

            return;
        }

        var applicationConnection = builder.Configuration.GetConnectionString("Application");
        var maintenanceConnection = builder.Configuration.GetConnectionString("Maintenance");

        // Fail-closed no startup: configuração inválida derruba o host antes de qualquer conexão.
        options.ValidateForOperation(applicationConnection, maintenanceConnection);
        var partitionPolicy = planningOptions.Enabled
            ? planningOptions.ValidateForOperation(inspectionEnabled: true)
            : null;

        RegisterServices(
            builder.Services, options, applicationConnection!, maintenanceConnection!, partitionPolicy);
    }

    private static void RegisterServices(
        IServiceCollection services,
        PstInspectionOptions options,
        string applicationConnection,
        string maintenanceConnection,
        PartitionPolicy? partitionPolicy)
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

        if (partitionPolicy is null)
        {
            return; // Planejamento desabilitado ⇒ nenhuma porta/caso de uso de particionamento é registrado.
        }

        // Passo 2 — planejamento read-only. O caso de uso NÃO recebe a engine: por construção, não há
        // caminho de código capaz de abrir/escrever o PST a partir do planejamento.
        IPartitionPlanStore planStore = new SqlPartitionPlanStore(connectionFactory);
        IPartitionPlanner planner = new SizeBoundedPartitionPlanner(partitionPolicy);
        services.AddSingleton(planStore);
        services.AddSingleton(planner);
        services.AddSingleton(new PlanPstPartitionUseCase(custodyStore, inspectionStore, planStore, planner, clock));
    }
}
