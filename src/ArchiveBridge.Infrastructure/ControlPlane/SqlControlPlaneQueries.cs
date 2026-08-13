using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Read-model de LEITURA do plano de controle. Cada consulta abre uma conexão da aplicação com o
/// <c>SESSION_CONTEXT('tenant_id')</c> do escopo (RLS por tenant) E filtra explicitamente por
/// <c>project_id = @project</c> — o isolamento é por tenant (RLS) E por projeto (filtro explícito), como
/// nas operações de Job dos Slices 1–3. Um usuário vinculado a um projeto nunca enxerga outro projeto do
/// mesmo tenant. Devolve apenas projeções leves de governança/observabilidade — nunca segredo, PST ou
/// evidência bruta, e nunca dispara execução. Os nomes de status vêm dos enums de domínio (fonte única).
/// </summary>
public sealed class SqlControlPlaneQueries(TenantConnectionFactory connectionFactory) : IControlPlaneQueries
{
    private const string DashboardSql =
        """
        WITH latest_ev AS (
            SELECT e.environment_id,
                   (SELECT TOP 1 d.status FROM dbo.ev_discovery_runs d
                    WHERE d.environment_id = e.environment_id AND d.project_id = @project AND d.status <> 6
                    ORDER BY d.discovery_version DESC) AS status
            FROM dbo.ev_environments e
            WHERE e.project_id = @project
        )
        SELECT
            (SELECT COUNT(*) FROM dbo.migration_projects WHERE project_id = @project),
            (SELECT COUNT(*) FROM dbo.migration_waves WHERE project_id = @project),
            (SELECT COUNT(*) FROM dbo.jobs WHERE project_id = @project),
            (SELECT COUNT(*) FROM dbo.jobs WHERE project_id = @project AND state = 0),
            (SELECT COUNT(*) FROM dbo.jobs WHERE project_id = @project AND state = 1),
            (SELECT COUNT(*) FROM dbo.jobs WHERE project_id = @project AND state = 4),
            (SELECT COUNT(*) FROM dbo.ev_environments WHERE project_id = @project),
            (SELECT COUNT(*) FROM latest_ev WHERE status = 2),
            (SELECT COUNT(*) FROM latest_ev WHERE status = 3),
            (SELECT COUNT(*) FROM latest_ev WHERE status = 4);
        """;

    private const string ProjectsSql =
        """
        SELECT project_id, project_name, project_owner, target_tenant, configuration_version, status, updated_at_utc
        FROM dbo.migration_projects
        WHERE project_id = @project
        ORDER BY project_name ASC;
        """;

    private const string WavesSql =
        """
        SELECT TOP (@max) wave_id, project_id, name, wave_version, status, planned_bytes, planned_items,
               created_at_utc, approved_at_utc, approved_by
        FROM dbo.migration_waves
        WHERE project_id = @project
        ORDER BY created_at_utc DESC, wave_id DESC;
        """;

    private const string JobsSql =
        """
        SELECT TOP (@max) job_id, project_id, workload, state, attempt_count, priority, owner_worker,
               last_error_code, updated_at_utc
        FROM dbo.jobs
        WHERE project_id = @project
        ORDER BY updated_at_utc DESC, job_id DESC;
        """;

    private const string TransitionsSql =
        """
        SELECT transition_id, from_state, to_state, reason_code, lease_epoch, worker_id, occurred_at_utc
        FROM dbo.job_state_transitions
        WHERE job_id = @jobId AND project_id = @project
        ORDER BY transition_id ASC;
        """;

    private const string EnvironmentsSql =
        """
        SELECT e.environment_id, e.site_name, e.directory_server,
               r.discovery_version, r.status, r.result_status, r.result_code, r.selected_adapter, r.adapter_version,
               r.observed_version, r.configuration_hash, r.evidence_hash, r.content_sha256,
               r.evidence_path, r.evidence_size_bytes, r.completed_at_utc
        FROM dbo.ev_environments e
        OUTER APPLY (
            SELECT TOP 1 d.discovery_version, d.status, d.result_status, d.result_code, d.selected_adapter,
                         d.adapter_version, d.observed_version, d.configuration_hash, d.evidence_hash,
                         d.content_sha256, d.evidence_path, d.evidence_size_bytes, d.completed_at_utc
            FROM dbo.ev_discovery_runs d
            WHERE d.environment_id = e.environment_id AND d.project_id = @project AND d.status <> 6
            ORDER BY d.discovery_version DESC
        ) r
        WHERE e.project_id = @project
        ORDER BY e.site_name ASC;
        """;

    private const string EvidenceSql =
        """
        SELECT TOP 1 environment_id, discovery_version, content_sha256, evidence_path, evidence_size_bytes
        FROM dbo.ev_discovery_runs
        WHERE environment_id = @environment
          AND discovery_version = @version
          AND project_id = @project
          AND status NOT IN (0, 1)
        ORDER BY discovery_version DESC;
        """;

    // HISTÓRICO paginado (keyset/seek). SQL ESTÁTICO: os filtros são predicados opcionais parametrizados
    // (@x IS NULL OR ...) e o seek é um predicado parametrizado — nenhum valor do usuário é concatenado. O
    // isolamento é por RLS (conexão tenant-scoped) E por project_id explícito (tanto em d quanto no JOIN e).
    // status NOT IN (0,1) exclui Pending/Discovering; Superseded (6) permanece visível no histórico.
    private const string EvidencePageSql =
        """
        SELECT TOP (@take)
               e.environment_id, e.site_name, d.discovery_version, d.status, d.result_status, d.result_code,
               d.configuration_hash, d.evidence_hash, d.content_sha256, d.evidence_path, d.evidence_size_bytes,
               d.completed_at_utc
        FROM dbo.ev_discovery_runs d
        INNER JOIN dbo.ev_environments e
            ON e.environment_id = d.environment_id AND e.tenant_id = d.tenant_id AND e.project_id = d.project_id
        WHERE d.project_id = @project
          AND e.project_id = @project
          AND d.status NOT IN (0, 1)
          AND (@environment IS NULL OR d.environment_id = @environment)
          AND (@resultStatus IS NULL OR d.result_status = @resultStatus)
          AND (@sitePrefix IS NULL OR e.site_name LIKE @sitePrefix ESCAPE N'\')
          AND (@fromUtc IS NULL OR d.completed_at_utc >= @fromUtc)
          AND (@toUtc IS NULL OR d.completed_at_utc <= @toUtc)
          AND (
                @cursorCompleted IS NULL
                OR d.completed_at_utc < @cursorCompleted
                OR (d.completed_at_utc = @cursorCompleted AND d.environment_id < @cursorEnvironment)
                OR (d.completed_at_utc = @cursorCompleted AND d.environment_id = @cursorEnvironment
                    AND d.discovery_version < @cursorVersion)
              )
        ORDER BY d.completed_at_utc DESC, d.environment_id DESC, d.discovery_version DESC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<DashboardSummary> GetDashboardAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(DashboardSql, tenant.Connection);
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DashboardSummary(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectSummary>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(ProjectsSql, tenant.Connection);
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(new ProjectSummary(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                ((ProjectStatus)reader.GetByte(5)).ToString(),
                SqlJobMapping.ReadUtc(reader.GetDateTime(6))));
        }

        return projects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WaveSummary>> ListWavesAsync(TenantScope scope, int max, CancellationToken cancellationToken)
    {
        var waves = new List<WaveSummary>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(WavesSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = Math.Clamp(max, 1, 500) });
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            waves.Add(new WaveSummary(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                ((WaveStatus)reader.GetByte(4)).ToString(),
                reader.GetInt64(5),
                reader.GetInt64(6),
                SqlJobMapping.ReadUtc(reader.GetDateTime(7)),
                reader.IsDBNull(8) ? null : SqlJobMapping.ReadUtc(reader.GetDateTime(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return waves;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSummary>> ListJobsAsync(TenantScope scope, int max, CancellationToken cancellationToken)
    {
        var jobs = new List<JobSummary>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(JobsSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = Math.Clamp(max, 1, 500) });
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(new JobSummary(
                reader.GetGuid(0),
                reader.GetGuid(1),
                ((Workload)reader.GetByte(2)).ToString(),
                ((JobState)reader.GetByte(3)).ToString(),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : ((ErrorCode)reader.GetByte(7)).ToString(),
                SqlJobMapping.ReadUtc(reader.GetDateTime(8))));
        }

        return jobs;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobTransitionView>> ListJobTransitionsAsync(
        TenantScope scope, Guid jobId, CancellationToken cancellationToken)
    {
        var transitions = new List<JobTransitionView>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(TransitionsSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId });
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new JobTransitionView(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : ((JobState)reader.GetByte(1)).ToString(),
                ((JobState)reader.GetByte(2)).ToString(),
                ((ReasonCode)reader.GetByte(3)).ToString(),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                SqlJobMapping.ReadUtc(reader.GetDateTime(6))));
        }

        return transitions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvEnvironmentSummary>> ListEvEnvironmentsAsync(
        TenantScope scope, CancellationToken cancellationToken)
    {
        var environments = new List<EvEnvironmentSummary>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(EnvironmentsSql, tenant.Connection);
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            EvDiscoverySummary? latest = null;
            if (!reader.IsDBNull(3))
            {
                latest = new EvDiscoverySummary(
                    reader.GetInt32(3),
                    ((EvDiscoveryStatus)reader.GetByte(4)).ToString(),
                    ((EvDiscoveryStatus)reader.GetByte(5)).ToString(),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.GetString(9),
                    reader.GetString(10).TrimEnd(),
                    reader.GetString(11).TrimEnd(),
                    reader.GetString(12).TrimEnd(),
                    reader.GetString(13),
                    reader.GetInt64(14),
                    SqlJobMapping.ReadUtc(reader.GetDateTime(15)));
            }

            environments.Add(new EvEnvironmentSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), latest));
        }

        return environments;
    }

    /// <inheritdoc />
    public async Task<EvEvidenceDownloadMetadata?> GetEvEvidenceAsync(
        TenantScope scope,
        Guid environmentId,
        int discoveryVersion,
        CancellationToken cancellationToken)
    {
        if (environmentId == Guid.Empty || discoveryVersion <= 0)
        {
            return null;
        }

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(EvidenceSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = discoveryVersion });
        AddProject(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new EvEvidenceDownloadMetadata(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetString(2).TrimEnd(),
            reader.GetString(3),
            reader.GetInt64(4));
    }

    /// <inheritdoc />
    public async Task<KeysetPage<EvEvidenceListItem, EvidenceSeekPosition>> SearchEvEvidencePageAsync(
        TenantScope scope,
        EvEvidenceFilter filter,
        int pageSize,
        EvidenceSeekPosition? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var bounded = KeysetPaging.ClampPageSize(pageSize);
        var fetched = new List<EvEvidenceListItem>();

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(EvidencePageSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = bounded + 1 });
        AddProject(command, scope);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier)
        { Value = (object?)filter.EnvironmentId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@resultStatus", SqlDbType.TinyInt)
        { Value = filter.ResultStatus is { } rs ? (byte)rs : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@sitePrefix", SqlDbType.NVarChar, 200)
        { Value = filter.SitePrefix is { } sp ? SqlLikePattern.EscapedPrefix(sp) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fromUtc", SqlDbType.DateTime2)
        { Value = filter.FromUtc is { } f ? f.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@toUtc", SqlDbType.DateTime2)
        { Value = filter.ToUtc is { } t ? t.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@cursorCompleted", SqlDbType.DateTime2)
        { Value = after is not null ? after.CompletedAtUtc.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@cursorEnvironment", SqlDbType.UniqueIdentifier)
        { Value = after is not null ? after.EnvironmentId : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@cursorVersion", SqlDbType.Int)
        { Value = after is not null ? after.DiscoveryVersion : DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fetched.Add(new EvEvidenceListItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                ((EvDiscoveryStatus)reader.GetByte(3)).ToString(),
                ((EvDiscoveryStatus)reader.GetByte(4)).ToString(),
                reader.GetString(5),
                reader.GetString(6).TrimEnd(),
                reader.GetString(7).TrimEnd(),
                reader.GetString(8).TrimEnd(),
                reader.GetString(9),
                reader.GetInt64(10),
                SqlJobMapping.ReadUtc(reader.GetDateTime(11))));
        }

        return KeysetPageBuilder.Build(fetched, bounded,
            last => new EvidenceSeekPosition(last.CompletedAtUtc, last.EnvironmentId, last.DiscoveryVersion));
    }

    private static void AddProject(SqlCommand command, TenantScope scope) =>
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
}
