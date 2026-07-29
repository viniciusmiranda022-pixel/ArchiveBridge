using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Jobs;

/// <summary>
/// Leitura da trilha de auditoria de transições (<c>job_state_transitions</c>) dentro do escopo de
/// tenant (RLS aplicada). Somente ids/códigos são expostos — nunca conteúdo sensível.
/// </summary>
public sealed class SqlJobAuditReader(TenantConnectionFactory connectionFactory) : IJobAuditReader
{
    private const string SelectSql =
        """
        SELECT job_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc
        FROM dbo.job_state_transitions
        WHERE job_id = @jobId
        ORDER BY transition_id ASC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobTransitionRecord>> GetTransitionsAsync(
        TenantScope scope,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        var transitions = new List<JobTransitionRecord>();

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectSql, tenantConnection.Connection);
        command.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new JobTransitionRecord(
                new JobId(reader.GetGuid(0)),
                reader.IsDBNull(1) ? null : (JobState)reader.GetByte(1),
                (JobState)reader.GetByte(2),
                (ReasonCode)reader.GetByte(3),
                new LeaseEpoch(reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : new WorkerId(reader.GetString(5)),
                new CorrelationId(reader.GetGuid(6)),
                SqlJobMapping.ReadUtc(reader.GetDateTime(7))));
        }

        return transitions;
    }
}
