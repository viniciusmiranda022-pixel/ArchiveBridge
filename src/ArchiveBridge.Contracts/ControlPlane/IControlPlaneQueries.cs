using ArchiveBridge.Contracts.Jobs;

namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>Contagens agregadas para o painel operacional (todas dentro do tenant do usuário, sob RLS).</summary>
public sealed record DashboardSummary(
    int Projects,
    int Waves,
    int Jobs,
    int JobsPending,
    int JobsProcessing,
    int JobsFailed,
    int EvEnvironments,
    int EvReady,
    int EvBlocked,
    int EvUnsupported);

/// <summary>Linha de projeto para a listagem (metadados de governança — sem segredo, sem conteúdo).</summary>
public sealed record ProjectSummary(
    Guid ProjectId,
    string Name,
    string Owner,
    string TargetTenant,
    int ConfigurationVersion,
    string Status,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Linha de onda para a listagem (capacidade planejada e situação de congelamento/aprovação).</summary>
public sealed record WaveSummary(
    Guid WaveId,
    Guid ProjectId,
    string Name,
    int WaveVersion,
    string Status,
    long PlannedBytes,
    long PlannedItems,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    string? ApprovedBy);

/// <summary>Linha de job durável para a listagem (estado, tentativas, dono e último erro).</summary>
public sealed record JobSummary(
    Guid JobId,
    Guid ProjectId,
    string Workload,
    string State,
    int AttemptCount,
    int Priority,
    string? OwnerWorker,
    string? LastErrorCode,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Transição de estado de um job (trilha de auditoria imutável, ordem cronológica).</summary>
public sealed record JobTransitionView(
    long TransitionId,
    string? FromState,
    string ToState,
    string ReasonCode,
    long LeaseEpoch,
    string? WorkerId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Ambiente Enterprise Vault e sua descoberta utilizável mais recente (READ-ONLY). O resultado
/// (<c>Ready</c>/<c>Blocked</c>/<c>Unsupported</c>) é sempre sustentado por evidência identificada pelos
/// hashes; <see langword="null"/> em <see cref="LatestDiscovery"/> indica que ainda não há descoberta.
/// </summary>
public sealed record EvEnvironmentSummary(
    Guid EnvironmentId,
    string SiteName,
    string DirectoryServer,
    EvDiscoverySummary? LatestDiscovery);

/// <summary>Metadados da descoberta (nunca a evidência bruta): versão, resultado, adapter e ponteiros de evidência.</summary>
public sealed record EvDiscoverySummary(
    int DiscoveryVersion,
    string Status,
    string ResultStatus,
    string ResultCode,
    string? SelectedAdapter,
    int? AdapterVersion,
    string ObservedVersion,
    string ConfigurationHash,
    string EvidenceHash,
    string EvidencePath,
    long EvidenceSizeBytes,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Read-model do plano de controle: consultas de LEITURA que sustentam o portal operacional. Toda consulta
/// carrega o <see cref="TenantScope"/> do usuário autenticado e executa sob a RLS por tenant (o cliente
/// nunca fornece tenant como autorização). Expõe apenas projeções leves de governança/observabilidade —
/// jamais segredo, SAS, token, PST ou evidência bruta. Não dispara nenhuma execução (Export-EVArchive,
/// Purview, Graph, AzCopy etc. permanecem fora deste slice).
/// </summary>
public interface IControlPlaneQueries
{
    /// <summary>Contagens agregadas do painel dentro do tenant do escopo.</summary>
    Task<DashboardSummary> GetDashboardAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Projetos do tenant (ordenados por nome).</summary>
    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Ondas do tenant (ordenadas por criação decrescente), limitadas a <paramref name="max"/>.</summary>
    Task<IReadOnlyList<WaveSummary>> ListWavesAsync(TenantScope scope, int max, CancellationToken cancellationToken);

    /// <summary>Jobs do tenant (ordenados por atualização decrescente), limitados a <paramref name="max"/>.</summary>
    Task<IReadOnlyList<JobSummary>> ListJobsAsync(TenantScope scope, int max, CancellationToken cancellationToken);

    /// <summary>Transições de um job dentro do escopo (ordem cronológica); vazio se inexistente/cross-tenant.</summary>
    Task<IReadOnlyList<JobTransitionView>> ListJobTransitionsAsync(TenantScope scope, Guid jobId, CancellationToken cancellationToken);

    /// <summary>Ambientes EV do tenant com sua descoberta utilizável mais recente (ordenados por site).</summary>
    Task<IReadOnlyList<EvEnvironmentSummary>> ListEvEnvironmentsAsync(TenantScope scope, CancellationToken cancellationToken);
}
