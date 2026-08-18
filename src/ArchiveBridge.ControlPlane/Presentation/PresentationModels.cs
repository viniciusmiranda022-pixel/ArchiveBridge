using ArchiveBridge.Contracts.ControlPlane;

namespace ArchiveBridge.ControlPlane.Presentation;

/// <summary>Situação visual de uma etapa do pipeline de migração.</summary>
public enum PipelineState
{
    /// <summary>Etapa concluída.</summary>
    Done,

    /// <summary>Etapa em andamento.</summary>
    Active,

    /// <summary>Etapa ainda pendente (implementada, sem trabalho iniciado).</summary>
    Pending,

    /// <summary>Etapa bloqueada.</summary>
    Blocked,

    /// <summary>Capacidade planejada para uma etapa futura (não executável nesta versão).</summary>
    Planned,

    /// <summary>Capacidade não disponível nesta versão do produto.</summary>
    Unavailable,
}

/// <summary>Uma etapa do pipeline de migração, com rótulo humano do estado.</summary>
public sealed record PipelineStage(int Number, string Name, PipelineState State, string StateLabel);

/// <summary>
/// Situação de um componente da plataforma. <see cref="BadgeVariant"/> é uma classe semântica de badge
/// (<c>success</c>/<c>warning</c>/<c>danger</c>/<c>info</c>/<c>neutral</c>) — nunca uma cor literal.
/// </summary>
public sealed record PlatformComponentStatus(string Name, string Detail, string BadgeVariant, string Label);

/// <summary>Uma linha de atividade recente (projeção de auditoria — nunca segredo nem bytes de evidência).</summary>
public sealed record ActivityEntry(
    DateTimeOffset WhenUtc,
    string Event,
    string Resource,
    bool? Succeeded,
    string Actor);

/// <summary>Um indicador numérico do painel. <see cref="Available"/> falso ⇒ a plataforma não expõe a contagem.</summary>
public sealed record DashboardMetric(string Key, string Label, string Value, bool Available);

/// <summary>Read-model de apresentação do painel (métricas + pipeline + status + atividade).</summary>
public sealed record DashboardViewModel(
    IReadOnlyList<DashboardMetric> Metrics,
    IReadOnlyList<PipelineStage> Pipeline,
    IReadOnlyList<PlatformComponentStatus> Platform,
    IReadOnlyList<ActivityEntry> Activity);

/// <summary>Rótulos de escopo exibidos no topo (tenant/projeto correntes). Somente exibição.</summary>
public sealed record PresentationScope(string TenantLabel, string ProjectLabel);

/// <summary>Detalhe de projeto para a tela de visão geral com abas.</summary>
public sealed record ProjectOverview(
    Guid ProjectId,
    string Name,
    string Status,
    string TargetTenant,
    string ArchivePolicy,
    int ConfigurationVersion,
    string ConfigurationHash,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<WaveSummary> Waves,
    IReadOnlyList<EvEnvironmentSummary> Environments,
    IReadOnlyList<JobSummary> Jobs);

/// <summary>Detalhe de onda com a linha do tempo do ciclo de vida.</summary>
public sealed record WaveDetail(
    Guid WaveId,
    string Name,
    int Version,
    string Status,
    long PlannedBytes,
    long PlannedItems,
    int PstCount,
    string TargetRootFolder,
    string MappingStatus,
    string? ApprovedBy,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PipelineStage> Lifecycle);

/// <summary>Uma versão histórica do mapping.</summary>
public sealed record MappingHistoryEntry(
    string Version,
    string Hash,
    int Rows,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    string GeneratedBy);

/// <summary>Read-model de apresentação da área de Mapping.</summary>
public sealed record MappingOverview(
    bool HasCurrent,
    string CurrentVersion,
    string Hash,
    int Rows,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    string GeneratedBy,
    IReadOnlyList<MappingHistoryEntry> History,
    bool PortalUploadEnabled);
