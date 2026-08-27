using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Jobs;

/// <summary>
/// Implementação SQL de <see cref="IPendingWorkRebuildQuery"/> (AB-I7-005 item 5). Reutiliza EXATAMENTE
/// o mesmo predicado de elegibilidade de <see cref="SqlJobStore"/> (<c>ClaimSql</c>) — <c>state IN
/// (Pending, RetryScheduled) AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc &lt;= @now)</c> — em
/// vez de o duplicar/reinterpretar. Leitura pura: nenhum lock de escrita (<c>UPDLOCK</c>/<c>READPAST</c>),
/// nenhuma mutação, nenhum efeito colateral — a reivindicação real continua exclusivamente via
/// <see cref="IJobStore.TryClaimNextAsync"/>. RLS por SESSION_CONTEXT garante isolamento entre tenants; o
/// filtro explícito por <c>project_id</c> garante isolamento entre projetos (mesmo padrão de
/// <see cref="SqlJobStore"/>).
/// </summary>
public sealed class SqlPendingWorkRebuildQuery(TenantConnectionFactory connectionFactory) : IPendingWorkRebuildQuery
{
    private const string RebuildSql =
        $"""
        SELECT {SqlJobMapping.JobColumns} FROM dbo.jobs
        WHERE project_id = @project
          AND workload = @workload
          AND state IN (0, 2)
          AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @asOf)
        ORDER BY next_attempt_at_utc ASC, created_at_utc ASC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSnapshot>> RebuildEligibleWorkAsync(
        TenantScope scope, Workload workload, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RebuildSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = (byte)workload });
        command.Parameters.Add(new SqlParameter("@asOf", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(asOfUtc) });

        var eligible = new List<JobSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            eligible.Add(SqlJobMapping.ReadSnapshot(reader));
        }

        return eligible;
    }
}
