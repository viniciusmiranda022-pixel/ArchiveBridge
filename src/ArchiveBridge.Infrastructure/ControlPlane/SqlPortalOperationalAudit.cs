using System.Data;
using ArchiveBridge.Contracts.ControlPlane;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ControlPlane;

/// <summary>
/// Auditoria operacional append-only. Toda conexão é aberta com o tenant do usuário (RLS) e toda consulta
/// também filtra explicitamente por projeto. Não registra bytes de evidência, segredo ou caminho físico.
/// </summary>
public sealed class SqlPortalOperationalAudit(TenantConnectionFactory connectionFactory) : IPortalOperationalAudit
{
    // Espelha o teto de comprimento do prefixo de usuário imposto no PageModel (Audit/Index). O parâmetro
    // @usernamePrefix é dimensionado pelo pior caso do escaping (ver SqlLikePattern.MaxEscapedPrefixLength)
    // para nunca truncar o padrão escapado.
    private const int UsernamePrefixRawMaxLength = 200;

    private const string InsertSql =
        """
        INSERT INTO dbo.portal_operational_audit_events
            (tenant_id, project_id, user_id, username, action_code, resource_type, resource_id,
             succeeded, reason, correlation_id, occurred_at_utc)
        VALUES
            (@tenant, @project, @user, @username, @action, @resourceType, @resourceId,
             @succeeded, @reason, @correlation, @occurred);
        """;

    private const string RecentSql =
        """
        SELECT TOP (@max) user_id, username, action_code, resource_type, resource_id,
               succeeded, reason, correlation_id, occurred_at_utc
        FROM dbo.portal_operational_audit_events
        WHERE project_id = @project
        ORDER BY occurred_at_utc DESC, event_id DESC;
        """;

    // Busca paginada (keyset/seek). SQL ESTÁTICO com predicados opcionais parametrizados e seek parametrizado;
    // nenhum valor do usuário é concatenado. Isolamento por RLS (conexão tenant-scoped) E project_id explícito.
    private const string SearchSql =
        """
        SELECT TOP (@take) event_id, user_id, username, action_code, resource_type, resource_id,
               succeeded, reason, correlation_id, occurred_at_utc
        FROM dbo.portal_operational_audit_events
        WHERE project_id = @project
          AND (@actionCode IS NULL OR action_code = @actionCode)
          AND (@usernamePrefix IS NULL OR username LIKE @usernamePrefix ESCAPE N'\')
          AND (@succeeded IS NULL OR succeeded = @succeeded)
          AND (@correlation IS NULL OR correlation_id = @correlation)
          AND (@fromUtc IS NULL OR occurred_at_utc >= @fromUtc)
          AND (@toUtc IS NULL OR occurred_at_utc <= @toUtc)
          AND (
                @cursorTime IS NULL
                OR occurred_at_utc < @cursorTime
                OR (occurred_at_utc = @cursorTime AND event_id < @cursorEventId)
              )
        ORDER BY occurred_at_utc DESC, event_id DESC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task RecordAsync(
        TenantScope scope,
        PortalOperationalAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@user", SqlDbType.UniqueIdentifier) { Value = auditEvent.UserId });
        command.Parameters.Add(new SqlParameter("@username", SqlDbType.NVarChar, 200) { Value = auditEvent.Username });
        command.Parameters.Add(new SqlParameter("@action", SqlDbType.NVarChar, 64) { Value = auditEvent.ActionCode });
        command.Parameters.Add(new SqlParameter("@resourceType", SqlDbType.NVarChar, 64) { Value = auditEvent.ResourceType });
        command.Parameters.Add(new SqlParameter("@resourceId", SqlDbType.NVarChar, 200) { Value = auditEvent.ResourceId });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit) { Value = auditEvent.Succeeded });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 100) { Value = auditEvent.Reason });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = auditEvent.CorrelationId });
        command.Parameters.Add(new SqlParameter("@occurred", SqlDbType.DateTime2) { Value = auditEvent.OccurredAtUtc.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalOperationalAuditEvent>> RecentAsync(
        TenantScope scope,
        int max,
        CancellationToken cancellationToken)
    {
        var events = new List<PortalOperationalAuditEvent>();
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(RecentSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = Math.Clamp(max, 1, 500) });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new PortalOperationalAuditEvent(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetString(6),
                reader.GetGuid(7),
                SqlJobMapping.ReadUtc(reader.GetDateTime(8))));
        }

        return events;
    }

    /// <inheritdoc />
    public async Task<KeysetPage<PortalOperationalAuditEvent, AuditSeekPosition>> SearchAsync(
        TenantScope scope,
        PortalOperationalAuditFilter filter,
        int pageSize,
        AuditSeekPosition? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var bounded = KeysetPaging.ClampPageSize(pageSize);
        var rows = new List<(PortalOperationalAuditEvent Event, long EventId)>();

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SearchSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = bounded + 1 });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@actionCode", SqlDbType.NVarChar, 64)
        { Value = filter.ActionCode is { } ac ? ac : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@usernamePrefix", SqlDbType.NVarChar,
            SqlLikePattern.MaxEscapedPrefixLength(UsernamePrefixRawMaxLength))
        { Value = filter.UsernamePrefix is { } up ? SqlLikePattern.EscapedPrefix(up) : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@succeeded", SqlDbType.Bit)
        { Value = filter.Succeeded is { } sc ? sc : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier)
        { Value = (object?)filter.CorrelationId ?? DBNull.Value });
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
            var evt = new PortalOperationalAuditEvent(
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetString(7),
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
        return new KeysetPage<PortalOperationalAuditEvent, AuditSeekPosition>(
            rows.Select(row => row.Event).ToList(), next, hasMore);
    }
}
