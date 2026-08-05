using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Auditoria de autenticação do portal em SQL Server (append-only). Registra toda tentativa — sucesso e
/// falha — com um motivo curto e não sensível; nunca grava senha, hash ou segredo. Usa a identidade da
/// aplicação por conexão simples (a tabela não está sob RLS por tenant).
/// </summary>
public sealed class SqlPortalSignInAudit(string connectionString) : IPortalSignInAudit
{
    private const string InsertSql =
        """
        INSERT INTO dbo.portal_sign_in_events (username, succeeded, reason, remote_address, occurred_at_utc)
        VALUES (@username, @succeeded, @reason, @remote, @occurred);
        """;

    private const string RecentSql =
        """
        SELECT TOP (@max) username, succeeded, reason, remote_address, occurred_at_utc
        FROM dbo.portal_sign_in_events
        ORDER BY occurred_at_utc DESC, event_id DESC;
        """;

    private readonly string _connectionString = connectionString;

    /// <inheritdoc />
    public async Task RecordAsync(PortalSignInEvent signInEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signInEvent);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, connection);
        command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = signInEvent.Username });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit) { Value = signInEvent.Succeeded });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100) { Value = signInEvent.Reason });
        command.Parameters.Add(new SqlParameter("@remote", SqlDbType.NVarChar, 100)
        {
            Value = (object?)signInEvent.RemoteAddress ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@occurred", SqlDbType.DateTime2)
        {
            Value = signInEvent.OccurredAtUtc.UtcDateTime,
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalSignInEvent>> RecentAsync(int max, CancellationToken cancellationToken)
    {
        var boundedMax = Math.Clamp(max, 1, 500);
        var events = new List<PortalSignInEvent>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RecentSql, connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = boundedMax });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new PortalSignInEvent(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                SqlJobMapping.ReadUtc(reader.GetDateTime(4))));
        }

        return events;
    }
}
