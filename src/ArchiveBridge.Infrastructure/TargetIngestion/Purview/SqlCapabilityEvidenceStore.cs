using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion;
using ArchiveBridge.Domain.TargetIngestion.Purview;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview;

/// <summary>
/// Store SQL append-only de <see cref="CapabilityEvidence"/>. O índice único
/// <c>(tenant_id, project_id, provider, route_key, version)</c> é o backstop de concorrência: duas
/// descobertas concorrentes calculando a MESMA próxima versão convergem quando o CONTEÚDO é idêntico
/// (<see cref="CapabilityEvidence.IsSameContentAs"/>) ou sinalizam <see cref="ConcurrencyException"/> em
/// vez de mascarar uma mudança real como réplay — mesmo padrão de <c>SqlConnectorInventoryStore</c>
/// (AB-4C-002). A leitura é fronteira NÃO CONFIÁVEL: <see cref="GetLatestAsync"/> reidrata via
/// <see cref="CapabilityEvidence.Rehydrate"/>, que recomputa o hash de adulteração e recusa fail-closed
/// qualquer divergência.
/// </summary>
public sealed class SqlCapabilityEvidenceStore(TenantConnectionFactory connectionFactory) : ICapabilityEvidenceStore
{
    private const string SelectLatestSql =
        """
        SELECT TOP (1) evidence_id, tenant_id, project_id, provider, route_key, version, status,
               source_reference, documentation_version, capability_version_label, observed_at_utc,
               correlation_id, recorded_at_utc, evidence_hash
        FROM dbo.purview_capability_evidence
        WHERE project_id = @project AND provider = @provider AND route_key = @route
        ORDER BY version DESC;
        """;

    private const string SelectByVersionSql =
        """
        SELECT evidence_id, tenant_id, project_id, provider, route_key, version, status,
               source_reference, documentation_version, capability_version_label, observed_at_utc,
               correlation_id, recorded_at_utc, evidence_hash
        FROM dbo.purview_capability_evidence
        WHERE project_id = @project AND provider = @provider AND route_key = @route AND version = @version;
        """;

    private const string InsertSql =
        """
        INSERT INTO dbo.purview_capability_evidence
            (evidence_id, tenant_id, project_id, provider, route_key, version, status, source_reference,
             documentation_version, capability_version_label, observed_at_utc, correlation_id,
             recorded_at_utc, evidence_hash)
        VALUES (@id, @tenant, @project, @provider, @route, @version, @status, @source, @docVersion,
                @capVersion, @observedAt, @correlation, @recordedAt, @hash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<CapabilityEvidence?> GetLatestAsync(
        TenantScope scope, TargetProvider provider, PurviewCapabilityRoute route, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectLatestSql, tenant.Connection);
        AddScopeParameters(command, scope, provider, route);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CapabilityEvidenceAppendResult> AppendAsync(CapabilityEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var scope = new TenantScope(evidence.Tenant, evidence.Project);

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(InsertSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = evidence.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = evidence.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = evidence.Project.Value });
        command.Parameters.Add(new SqlParameter("@provider", SqlDbType.TinyInt) { Value = (byte)evidence.Provider });
        command.Parameters.Add(new SqlParameter("@route", SqlDbType.NVarChar, 200) { Value = evidence.Route.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = evidence.Version });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)evidence.Status });
        command.Parameters.Add(new SqlParameter("@source", SqlDbType.NVarChar, 400) { Value = (object?)evidence.SourceReference ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@docVersion", SqlDbType.NVarChar, 100) { Value = (object?)evidence.DocumentationVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@capVersion", SqlDbType.NVarChar, 100) { Value = (object?)evidence.CapabilityVersionLabel ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(evidence.ObservedAtUtc) });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = evidence.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(evidence.RecordedAtUtc) });
        command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = evidence.EvidenceHash.Value });

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new CapabilityEvidenceAppendResult(evidence, Created: true);
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627)
        {
            // Colisão de versão: outra descoberta concorrente do MESMO escopo/rota já gravou esta versão.
            // Converge (Created=false) SOMENTE se o conteúdo lógico bater; conteúdo diferente é concorrência
            // real — nunca mascarada como réplay.
            var existing = await GetByVersionAsync(scope, evidence.Provider, evidence.Route, evidence.Version, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            if (existing.IsSameContentAs(evidence))
            {
                return new CapabilityEvidenceAppendResult(existing, Created: false);
            }

            throw new ConcurrencyException(
                $"Rota {evidence.Route.Value}: a versão {evidence.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                "já foi ocupada por outra descoberta concorrente com conteúdo diferente. Releia o latest e tente novamente.");
        }
    }

    private async Task<CapabilityEvidence?> GetByVersionAsync(
        TenantScope scope, TargetProvider provider, PurviewCapabilityRoute route, int version, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectByVersionSql, tenant.Connection);
        AddScopeParameters(command, scope, provider, route);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static void AddScopeParameters(SqlCommand command, TenantScope scope, TargetProvider provider, PurviewCapabilityRoute route)
    {
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@provider", SqlDbType.TinyInt) { Value = (byte)provider });
        command.Parameters.Add(new SqlParameter("@route", SqlDbType.NVarChar, 200) { Value = route.Value });
    }

    private static async Task<CapabilityEvidence?> ReadOneAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return CapabilityEvidence.Rehydrate(
            new CapabilityEvidenceId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            (TargetProvider)reader.GetByte(3),
            new PurviewCapabilityRoute(reader.GetString(4)),
            reader.GetInt32(5),
            (CapabilityStatus)reader.GetByte(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            SqlJobMapping.ReadUtc(reader.GetDateTime(10)),
            new CorrelationId(reader.GetGuid(11)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
            new Sha256Hash(reader.GetString(13)));
    }
}
