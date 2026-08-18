using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>
/// Provedor de dados do Modo de Demonstração. Vive SOMENTE no Control Plane (concern de UI) — nunca no Domain,
/// Application ou nos stores de negócio da Infrastructure. É estritamente READ-ONLY e em memória: devolve um
/// dataset 100% SINTÉTICO para as telas quando <see cref="PresentationModeOptions.Enabled"/> está ativo. Não
/// possui NENHUM método de escrita e não acessa SQL, filesystem, Worker nem PowerShell.
/// </summary>
public interface IPresentationDataProvider
{
    /// <summary>Rótulos de escopo sintéticos (tenant/projeto) exibidos no topo durante a demonstração.</summary>
    PresentationScope Scope { get; }

    /// <summary>Painel sintético (métricas, pipeline, status da plataforma, atividade recente).</summary>
    DashboardViewModel GetDashboard();

    /// <summary>Projetos sintéticos.</summary>
    IReadOnlyList<ProjectSummary> GetProjects();

    /// <summary>Visão geral de um projeto sintético; <see langword="null"/> se o identificador não existir no dataset.</summary>
    ProjectOverview? GetProject(Guid projectId);

    /// <summary>Ondas sintéticas.</summary>
    IReadOnlyList<WaveSummary> GetWaves();

    /// <summary>Detalhe de uma onda sintética; <see langword="null"/> se o identificador não existir no dataset.</summary>
    WaveDetail? GetWave(Guid waveId);

    /// <summary>Jobs sintéticos.</summary>
    IReadOnlyList<JobSummary> GetJobs();

    /// <summary>Transições sintéticas de um job; vazio se o identificador não existir no dataset.</summary>
    IReadOnlyList<JobTransitionView> GetJobTransitions(Guid jobId);

    /// <summary>Ambientes Enterprise Vault sintéticos com a descoberta mais recente.</summary>
    IReadOnlyList<EvEnvironmentSummary> GetEnvironments();

    /// <summary>Histórico de evidências sintético.</summary>
    IReadOnlyList<EvEvidenceListItem> GetEvidence();

    /// <summary>Eventos operacionais sintéticos.</summary>
    IReadOnlyList<PortalOperationalAuditEvent> GetOperationalAudit();

    /// <summary>Eventos de autenticação sintéticos.</summary>
    IReadOnlyList<PortalSignInEvent> GetSignIns();

    /// <summary>Visão geral sintética da área de Mapping.</summary>
    MappingOverview GetMapping();
}
