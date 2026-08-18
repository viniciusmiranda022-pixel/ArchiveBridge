using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Compõe o read-model de apresentação do painel a partir dos dados REAIS do read-model (fora do modo de
/// demonstração). É honesto por construção: só exibe contagens que a plataforma realmente expõe (as demais
/// aparecem como indisponíveis, nunca inventadas) e o status da plataforma reflete apenas o que é observável
/// nesta requisição — o restante fica como "Não monitorado"/"Não configurado", jamais um "Operacional" sem
/// evidência.
/// </summary>
public static class RealDashboardComposer
{
    /// <summary>Monta o painel real a partir do resumo agregado e dos eventos operacionais recentes.</summary>
    public static DashboardViewModel Build(
        DashboardSummary summary,
        IReadOnlyList<PortalOperationalAuditEvent> recentOperations)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(recentOperations);

        var metrics = new List<DashboardMetric>
        {
            new("projects", "Projetos", summary.Projects.ToString(System.Globalization.CultureInfo.InvariantCulture), true),
            new("waves", "Ondas", summary.Waves.ToString(System.Globalization.CultureInfo.InvariantCulture), true),
            new("environments", "Ambientes EV", summary.EvEnvironments.ToString(System.Globalization.CultureInfo.InvariantCulture), true),
            new("jobs", "Jobs em execução", summary.JobsProcessing.ToString(System.Globalization.CultureInfo.InvariantCulture), true),
            // A plataforma ainda não expõe contagem agregada de evidências/validações no read-model: indisponível
            // (nunca um número inventado).
            new("evidence", "Evidências", "—", false),
            new("validations", "Validações", "—", false),
        };

        // Descoberta é a capacidade viva desta versão; as demais etapas são pendentes/planejadas/indisponíveis.
        var discoveryState = summary.EvEnvironments > 0 ? PipelineState.Active : PipelineState.Pending;
        var pipeline = new List<PipelineStage>
        {
            new(1, "Descoberta", discoveryState, discoveryState == PipelineState.Active ? "Em andamento" : "Pendente"),
            new(2, "Planejamento", PipelineState.Pending, "Pendente"),
            new(3, "Mapping", PipelineState.Pending, "Pendente"),
            new(4, "Validação", PipelineState.Pending, "Pendente"),
            new(5, "Exportação", PipelineState.Planned, "Planejado — próxima etapa"),
            new(6, "Staging", PipelineState.Unavailable, "Não disponível nesta versão"),
            new(7, "Importação M365", PipelineState.Unavailable, "Não disponível nesta versão"),
            new(8, "Reconciliação", PipelineState.Planned, "Planejado — próxima etapa"),
        };

        var platform = new List<PlatformComponentStatus>
        {
            new("Control Plane", "Portal operacional on-premises", "success", "Operacional"),
            new("SQL Server", "Consulta do painel respondida nesta requisição", "success", "Conectado"),
            new("Enterprise Vault Worker", "Sem sonda de saúde nesta versão", "neutral", "Não monitorado"),
            new("Evidence Store", "Sem sonda de saúde nesta versão", "neutral", "Não monitorado"),
            new("Microsoft 365 Connector", "Importação — etapa futura", "neutral", "Não configurado"),
        };

        var activity = recentOperations
            .Take(8)
            .Select(e => new ActivityEntry(
                e.OccurredAtUtc,
                DescribeAction(e.ActionCode, e.Reason),
                $"{e.ResourceType}/{e.ResourceId}",
                e.Succeeded,
                e.Username))
            .ToList();

        return new DashboardViewModel(metrics, pipeline, platform, activity);
    }

    private static string DescribeAction(string actionCode, string reason) => actionCode switch
    {
        "ev.discovery.request" => "Solicitação de descoberta",
        "evidence.download" => "Download de evidência",
        _ => string.IsNullOrEmpty(reason) ? actionCode : $"{actionCode} ({reason})",
    };
}
