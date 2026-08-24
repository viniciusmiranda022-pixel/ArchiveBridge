using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.Upload;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.Upload;

/// <summary>
/// Projeção SQL (somente leitura) dos escopos com trabalho de UPLOAD Purview elegível — mesmo padrão de
/// <c>SqlEvExportPendingScopeReader</c>: usa a identidade de MANUTENÇÃO, é ESTRITAMENTE READ-ONLY (nenhum
/// claim/UPDATE/INSERT/efeito de negócio) e devolve apenas (tenant, projeto) DISTINTO.
/// </summary>
public sealed class SqlPurviewUploadPendingScopeReader(TenantConnectionFactory connectionFactory, IClock clock) : IPurviewUploadPendingScopeReader
{
    private const byte UploadWorkload = (byte)Workload.Upload;
    private const byte PendingState = (byte)JobState.Pending;
    private const byte RetryScheduledState = (byte)JobState.RetryScheduled;

    private const string SelectEligibleScopesSql =
        """
        SET NOCOUNT ON;
        SELECT DISTINCT TOP (@max) j.tenant_id, j.project_id
        FROM dbo.jobs j
        WHERE j.workload = @workload
          AND j.state IN (@pending, @retryScheduled)
          AND (j.next_attempt_at_utc IS NULL OR j.next_attempt_at_utc <= @now)
          AND EXISTS (
              SELECT 1 FROM dbo.purview_upload_requests pur
              WHERE pur.job_id = j.job_id AND pur.tenant_id = j.tenant_id AND pur.project_id = j.project_id)
        ORDER BY j.tenant_id, j.project_id;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantScope>> ListEligibleScopesAsync(int max, CancellationToken cancellationToken)
    {
        if (max <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "O limite de escopos deve ser > 0.");
        }

        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);
        var scopes = new List<TenantScope>();

        await using var maintenance = await _connectionFactory.OpenForMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectEligibleScopesSql, maintenance.Connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
        command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = UploadWorkload });
        command.Parameters.Add(new SqlParameter("@pending", SqlDbType.TinyInt) { Value = PendingState });
        command.Parameters.Add(new SqlParameter("@retryScheduled", SqlDbType.TinyInt) { Value = RetryScheduledState });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopes.Add(new TenantScope(new TenantId(reader.GetGuid(0)), new ProjectId(reader.GetGuid(1))));
        }

        return scopes;
    }
}
