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
/// <c>SESSION_CONTEXT('tenant_id')</c> do escopo (RLS por tenant) e devolve projeções leves de governança
/// e observabilidade — nunca segredo, PST ou evidência bruta, e nunca dispara execução. Os nomes de status
/// vêm dos enums de domínio (o TINYINT persistido é convertido para o nome canônico), garantindo uma única
/// fonte de verdade para os rótulos exibidos.
/// </summary>
public sealed class SqlControlPlaneQueries(TenantConnectionFactory connectionFactory) : IControlPlaneQueries
{
    private const string DashboardSql =
        """
        WITH latest_ev AS (
            SELECT e.environment_id,
                   (SELECT TOP 1 d.status FROM dbo.ev_discovery_runs d
                    WHERE d.environment_id = e.environment_id AND d.status <> 6
                    ORDER BY d.discovery_version DESC) AS status
            FROM dbo.ev_environments e
        )
        SELECT
            (SELECT COUNT(*) FROM dbo.migration_projects),
            (SELECT COUNT(*) FROM dbo.migration_waves),
            (SELECT COUNT(*) FROM dbo.jobs),
            (SELECT COUNT(*) FROM dbo.jobs WHERE state = 0),
            (SELECT COUNT(*) FROM dbo.jobs WHERE state = 1),
            (SELECT COUNT(*) FROM dbo.jobs WHERE state = 4),
            (SELECT COUNT(*) FROM dbo.ev_environments),
            (SELECT COUNT(*) FROM latest_ev WHERE status = 2),
            (SELECT COUNT(*) FROM latest_ev WHERE status = 3),
            (SELECT COUNT(*) FROM latest_ev WHERE status = 4);
        """;

    private const string ProjectsSql =
        """
        SELECT project_id, project_name, project_owner, target_tenant, configuration_version, status, updated_at_utc
        FROM dbo.migration_projects
        ORDER BY project_name ASC;
        """;

    private const string WavesSql =
        """
        SELECT TOP (@max) wave_id, project_id, name, wave_version, status, planned_bytes, planned_items,
               created_at_utc, approved_at_utc, approved_by
        FROM dbo.migration_waves
        ORDER BY created_at_utc DESC, wave_id DESC;
        """;

    private const string JobsSql =
        """
        SELECT TOP (@max) job_id, project_id, workload, state, attempt_count, priority, owner_worker,
               last_error_code, updated_at_utc
        FROM dbo.jobs
        ORDER BY updated_at_utc DESC, job_id DESC;
        """;

    private const string TransitionsSql =
        """
        SELECT transition_id, from_state, to_state, reason_code, lease_epoch, worker_id, occurred_at_utc
        FROM dbo.job_state_transitions
        WHERE job_id = @jobId
        ORDER BY transition_id ASC;
        """;

    private const string EnvironmentsSql =
        """
        SELECT e.environment_id, e.site_name, e.directory_server,
               r.discovery_version, r.status, r.result_status, r.result_code, r.selected_adapter, r.adapter_version,
               r.observed_version, r.configuration_hash, r.evidence_hash, r.evidence_path, r.evidence_size_bytes,
               r.completed_at_utc
        FROM dbo.ev_environments e
        OUTER APPLY (
            SELECT TOP 1 d.discovery_version, d.status, d.result_status, d.result_code, d.selected_adapter,
                         d.adapter_version, d.observed_version, d.configuration_hash, d.evidence_hash,
                         d.evidence_path, d.evidence_size_bytes, d.completed_at_utc
            FROM dbo.ev_discovery_runs d
            WHERE d.environment_id = e.environment_id AND d.status <> 6
            ORDER BY d.discovery_version DESC
        ) r
        ORDER BY e.site_name ASC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<DashboardSummary> GetDashboardAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(DashboardSql, tenant.Connection);
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
                    reader.GetString(12),
                    reader.GetInt64(13),
                    SqlJobMapping.ReadUtc(reader.GetDateTime(14)));
            }

            environments.Add(new EvEnvironmentSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), latest));
        }

        return environments;
    }
}
