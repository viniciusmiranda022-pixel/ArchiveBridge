using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Delta;

/// <summary>
/// Store SQL append-only de watermarks (AB-4C-008 req 3/4/5). A leitura é fronteira NÃO CONFIÁVEL (mesmo
/// princípio de <c>InventorySnapshot.Rehydrate</c>): <see cref="GetLatestCanonicalAsync"/>/<see cref="GetByIdAsync"/>
/// reidratam via <see cref="EvWatermark.Rehydrate"/>, que recomputa o hash de lineage a partir dos campos
/// REALMENTE carregados — nenhuma linha adulterada é devolvida como watermark canônico.
/// </summary>
public sealed class SqlEvWatermarkStore(TenantConnectionFactory connectionFactory) : IEvWatermarkStore
{
    private const string Columns =
        "watermark_id, tenant_id, project_id, connector_id, external_archive_id, phase, strategy_name, " +
        "strategy_version, producing_execution_id, opaque_token, lineage_hash, issued_at_utc";

    private const string InsertSql =
        $"""
        INSERT INTO dbo.ev_watermarks
            ({Columns})
        VALUES
            (@id, @tenant, @project, @connector, @archiveId, @phase, @strategyName, @strategyVersion,
             @executionId, @token, @lineageHash, @issuedAt);
        """;

    private const string SelectLatestSql =
        $"""
        SELECT TOP (1) {Columns}
        FROM dbo.ev_watermarks
        WHERE tenant_id = @tenant AND project_id = @project AND connector_id = @connector AND external_archive_id = @archiveId
        ORDER BY issued_at_utc DESC;
        """;

    private const string SelectByIdSql =
        $"""
        SELECT {Columns}
        FROM dbo.ev_watermarks
        WHERE watermark_id = @id AND tenant_id = @tenant AND project_id = @project;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task AppendAsync(TenantScope scope, EvWatermark watermark, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        BindWatermark(command, watermark);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvWatermark?> GetLatestCanonicalAsync(
        TenantScope scope, ConnectorId connector, string externalArchiveId, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectLatestSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = externalArchiveId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadWatermark(reader) : null;
    }

    /// <inheritdoc />
    public async Task<EvWatermark?> GetByIdAsync(TenantScope scope, WatermarkId id, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectByIdSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadWatermark(reader) : null;
    }

    private static void BindWatermark(SqlCommand command, EvWatermark watermark)
    {
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = watermark.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = watermark.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = watermark.Project.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = watermark.Connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = watermark.ExternalArchiveId });
        command.Parameters.Add(new SqlParameter("@phase", SqlDbType.TinyInt) { Value = (byte)watermark.Phase });
        command.Parameters.Add(new SqlParameter("@strategyName", SqlDbType.NVarChar, 100) { Value = watermark.Strategy.Name });
        command.Parameters.Add(new SqlParameter("@strategyVersion", SqlDbType.Int) { Value = watermark.Strategy.Version });
        command.Parameters.Add(new SqlParameter("@executionId", SqlDbType.UniqueIdentifier) { Value = watermark.ProducingExecutionId });
        command.Parameters.Add(new SqlParameter("@token", SqlDbType.NVarChar, 4000) { Value = watermark.OpaqueToken });
        command.Parameters.Add(new SqlParameter("@lineageHash", SqlDbType.Char, 64) { Value = watermark.LineageHash.Value });
        command.Parameters.Add(new SqlParameter("@issuedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(watermark.IssuedAtUtc) });
    }

    private static EvWatermark ReadWatermark(SqlDataReader reader) =>
        EvWatermark.Rehydrate(
            new WatermarkId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new ConnectorId(reader.GetGuid(3)),
            reader.GetString(4),
            (EvDeltaPhase)reader.GetByte(5),
            new EvDeltaStrategyId(reader.GetString(6), reader.GetInt32(7)),
            reader.GetGuid(8),
            reader.GetString(9),
            SqlJobMapping.ReadUtc(reader.GetDateTime(11)),
            new Sha256Hash(reader.GetString(10)));
}
