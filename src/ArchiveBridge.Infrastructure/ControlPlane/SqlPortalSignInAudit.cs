using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Auditoria de autenticação do portal em SQL Server (append-only). Registra toda tentativa — sucesso e
/// falha — com um motivo curto e não sensível; nunca grava senha, hash ou segredo. A LEITURA é escopada
/// por tenant: como a tabela não está sob a RLS por tenant, o filtro explícito <c>tenant_id = @tenant</c>
/// é o que impede vazamento cross-tenant. Usa a identidade da aplicação por conexão simples.
/// </summary>
public sealed class SqlPortalSignInAudit(string connectionString) : IPortalSignInAudit
{
    // Espelha o teto de comprimento do prefixo de usuário imposto no PageModel (Audit/Index). O parâmetro
    // @usernamePrefix é dimensionado pelo pior caso do escaping (ver SqlLikePattern.MaxEscapedPrefixLength)
    // para nunca truncar o padrão escapado.
    private const int UsernamePrefixRawMaxLength = 200;

    private const string InsertSql =
        """
        INSERT INTO dbo.portal_sign_in_events
            (tenant_id, project_id, user_id, username, succeeded, reason, remote_address, correlation_id, occurred_at_utc)
        VALUES (@tenant, @project, @user, @username, @succeeded, @reason, @remote, @correlation, @occurred);
        """;

    private const string RecentSql =
        """
        SELECT TOP (@max) tenant_id, project_id, user_id, username, succeeded, reason, remote_address,
               correlation_id, occurred_at_utc
        FROM dbo.portal_sign_in_events
        WHERE tenant_id = @tenant
        ORDER BY occurred_at_utc DESC, event_id DESC;
        """;

    // Busca paginada (keyset/seek). O filtro obrigatório tenant_id = @tenant (a tabela NÃO está sob RLS) é o
    // que impede vazamento cross-tenant; o tenant nunca vem do cliente. SQL estático + predicados
    // parametrizados; nenhum valor do usuário é concatenado. Eventos com tenant nulo não casam este filtro.
    private const string SearchSql =
        """
        SELECT TOP (@take) event_id, tenant_id, project_id, user_id, username, succeeded, reason, remote_address,
               correlation_id, occurred_at_utc
        FROM dbo.portal_sign_in_events
        WHERE tenant_id = @tenant
          AND (@usernamePrefix IS NULL OR username LIKE @usernamePrefix ESCAPE N'\')
          AND (@succeeded IS NULL OR succeeded = @succeeded)
          AND (@reason IS NULL OR reason = @reason)
          AND (@fromUtc IS NULL OR occurred_at_utc >= @fromUtc)
          AND (@toUtc IS NULL OR occurred_at_utc <= @toUtc)
          AND (
                @cursorTime IS NULL
                OR occurred_at_utc < @cursorTime
                OR (occurred_at_utc = @cursorTime AND event_id < @cursorEventId)
              )
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
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = (object?)signInEvent.TenantId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = (object?)signInEvent.ProjectId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@user", SqlDbType.UniqueIdentifier) { Value = (object?)signInEvent.UserId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = signInEvent.Username });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit) { Value = signInEvent.Succeeded });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100) { Value = signInEvent.Reason });
        command.Parameters.Add(new SqlParameter("@remote", SqlDbType.NVarChar, 100) { Value = (object?)signInEvent.RemoteAddress ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = signInEvent.CorrelationId });
        command.Parameters.Add(new SqlParameter("@occurred", SqlDbType.DateTime2) { Value = signInEvent.OccurredAtUtc.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalSignInEvent>> RecentAsync(TenantId tenant, int max, CancellationToken cancellationToken)
    {
        var boundedMax = Math.Clamp(max, 1, 500);
        var events = new List<PortalSignInEvent>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RecentSql, connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = boundedMax });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new PortalSignInEvent(
                reader.IsDBNull(0) ? null : reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetGuid(7),
                SqlJobMapping.ReadUtc(reader.GetDateTime(8))));
        }

        return events;
    }

    /// <inheritdoc />
    public async Task<KeysetPage<PortalSignInEvent, AuditSeekPosition>> SearchAsync(
        TenantId tenant,
        PortalSignInAuditFilter filter,
        int pageSize,
        AuditSeekPosition? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var bounded = KeysetPaging.ClampPageSize(pageSize);
        var rows = new List<(PortalSignInEvent Event, long EventId)>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SearchSql, connection);
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = bounded + 1 });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant.Value });
        command.Parameters.Add(new SqlParameter("@usernamePrefix", SqlDbType.NVarChar,
            SqlLikePattern.MaxEscapedPrefixLength(UsernamePrefixRawMaxLength))
        { Value = filter.UsernamePrefix is { } up ? SqlLikePattern.EscapedPrefix(up) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit)
        { Value = filter.Succeeded is { } sc ? sc : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100)
        { Value = filter.Reason is { } rn ? rn : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@fromUtc", SqlDbType.DateTime2)
        { Value = filter.FromUtc is { } f ? f.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@toUtc", SqlDbType.DateTime2)
        { Value = filter.ToUtc is { } t ? t.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@cursorTime", SqlDbType.DateTime2)
        { Value = after is not null ? after.OccurredAtUtc.UtcDateTime : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@cursorEventId", SqlDbType.BigInt)
        { Value = after is not null ? after.EventId : DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var evt = new PortalSignInEvent(
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetGuid(8),
                SqlJobMapping.ReadUtc(reader.GetDateTime(9)));
            rows.Add((evt, reader.GetInt64(0)));
        }

        var hasMore = rows.Count > bounded;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore && rows.Count > 0
            ? new AuditSeekPosition(rows[^1].Event.OccurredAtUtc, rows[^1].EventId)
            : null;
        return new KeysetPage<PortalSignInEvent, AuditSeekPosition>(
            rows.Select(row => row.Event).ToList(), next, hasMore);
    }
}
