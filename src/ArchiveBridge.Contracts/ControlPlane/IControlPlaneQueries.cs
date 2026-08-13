using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.ControlPlane;

/// <summary>Contagens agregadas para o painel operacional (todas dentro do tenant/projeto do usuário).</summary>
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

/// <summary>
/// Metadados da descoberta (nunca a evidência bruta). <see cref="EvidenceHash"/> é o hash SEMÂNTICO da
/// evidência; <see cref="ContentSha256"/> é o SHA-256 dos bytes exatos de <c>evidence.json</c>.
/// </summary>
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
    string ContentSha256,
    string EvidencePath,
    long EvidenceSizeBytes,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Âncora autoritativa para um download de evidência. Só é retornada quando ambiente+versão pertencem ao
/// tenant/projeto autenticado e a execução já saiu dos estados de reserva/execução. O conteúdo NÃO vem do SQL.
/// </summary>
public sealed record EvEvidenceDownloadMetadata(
    Guid EnvironmentId,
    int DiscoveryVersion,
    string ContentSha256,
    string EvidencePath,
    long EvidenceSizeBytes);

/// <summary>
/// Filtros do HISTÓRICO de evidências de descoberta. Todos opcionais; valores textuais são normalizados
/// (trim/tamanho) e nunca viram sintaxe SQL. O tenant/projeto NÃO fazem parte do filtro — são resolvidos
/// server-side a partir do principal. <see cref="ResultStatus"/> é um enum fechado do domínio.
/// </summary>
public sealed record EvEvidenceFilter(
    string? SitePrefix,
    Guid? EnvironmentId,
    EvDiscoveryStatus? ResultStatus,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

/// <summary>
/// Item do HISTÓRICO de evidências (uma linha por execução de descoberta madura). Só metadados — nunca bytes
/// de <c>evidence.json</c>, stdout/stderr ou credenciais. <see cref="LifecycleStatus"/> distingue o ciclo de
/// vida (inclusive <c>Superseded</c>) de <see cref="ResultStatus"/>.
/// </summary>
public sealed record EvEvidenceListItem(
    Guid EnvironmentId,
    string SiteName,
    int DiscoveryVersion,
    string LifecycleStatus,
    string ResultStatus,
    string ResultCode,
    string ConfigurationHash,
    string EvidenceHash,
    string ContentSha256,
    string EvidencePath,
    long EvidenceSizeBytes,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Read-model do plano de controle: consultas de LEITURA que sustentam o portal operacional. Toda consulta
/// carrega o <see cref="TenantScope"/> do usuário autenticado, executa sob a RLS por tenant e filtra o projeto
/// explicitamente. Expõe apenas projeções leves de governança/observabilidade — jamais segredo, SAS, token,
/// PST ou evidência bruta. Não dispara nenhuma execução (Export-EVArchive, Purview, Graph, AzCopy etc.).
/// </summary>
public interface IControlPlaneQueries
{
    /// <summary>Contagens agregadas do painel dentro do tenant/projeto do escopo.</summary>
    Task<DashboardSummary> GetDashboardAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Projeto do escopo autenticado.</summary>
    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>Ondas do projeto autenticado, limitadas a <paramref name="max"/>.</summary>
    Task<IReadOnlyList<WaveSummary>> ListWavesAsync(TenantScope scope, int max, CancellationToken cancellationToken);

    /// <summary>Jobs do projeto autenticado, limitados a <paramref name="max"/>.</summary>
    Task<IReadOnlyList<JobSummary>> ListJobsAsync(TenantScope scope, int max, CancellationToken cancellationToken);

    /// <summary>Transições de um job dentro do escopo; vazio se inexistente/cross-tenant/cross-project.</summary>
    Task<IReadOnlyList<JobTransitionView>> ListJobTransitionsAsync(TenantScope scope, Guid jobId, CancellationToken cancellationToken);

    /// <summary>Ambientes EV do projeto autenticado com sua descoberta utilizável mais recente.</summary>
    Task<IReadOnlyList<EvEnvironmentSummary>> ListEvEnvironmentsAsync(TenantScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve os metadados autoritativos de uma evidência por ambiente/versão no escopo autenticado.
    /// Retorna <see langword="null"/> para inexistente, cross-tenant ou cross-project (anti-IDOR).
    /// </summary>
    Task<EvEvidenceDownloadMetadata?> GetEvEvidenceAsync(
        TenantScope scope,
        Guid environmentId,
        int discoveryVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// HISTÓRICO paginado (keyset/seek) de evidências de descoberta do escopo autenticado — execuções maduras
    /// (<c>status NOT IN (Pending, Discovering)</c>), inclusive <c>Superseded</c>. Ordem determinística
    /// <c>completed_at_utc</c> DESC, <c>environment_id</c> DESC, <c>discovery_version</c> DESC. Sob RLS por
    /// tenant + filtro explícito por projeto (também no JOIN com <c>ev_environments</c>). O
    /// <paramref name="after"/> é a posição de seek já validada; nunca carrega escopo.
    /// </summary>
    Task<KeysetPage<EvEvidenceListItem, EvidenceSeekPosition>> SearchEvEvidencePageAsync(
        TenantScope scope,
        EvEvidenceFilter filter,
        int pageSize,
        EvidenceSeekPosition? after,
        CancellationToken cancellationToken);
}
