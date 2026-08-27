using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Security;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Security;
using ArchiveBridge.Infrastructure.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Security;

/// <summary>
/// Persistência do <see cref="WdacPolicyEvidence"/> (AB-I7-008). As entradas da allowlist são codificadas
/// canonicamente em UMA coluna (<c>U+001F</c> separa os campos de uma entrada, <c>U+001E</c> separa
/// entradas — ambos caracteres de controle, sempre recusados dentro dos próprios campos por
/// <c>TextValue.Require</c>, portanto sem ambiguidade de parsing). <see cref="WdacPolicyEvidence.Rehydrate"/>
/// recomputa <c>policy_digest</c> a partir das entradas REALMENTE decodificadas — uma adulteração da
/// coluna <c>entries_canonical</c> é detectada como tampering.
/// </summary>
public sealed class SqlWdacPolicyEvidenceStore(TenantConnectionFactory connectionFactory) : IWdacPolicyEvidenceStore
{
    private const char FieldSeparator = '\u001F';
    private const char EntrySeparator = '\u001E';

    // Colunas = tenant_id(0), project_id(1), policy_version(2), entries_canonical(3), policy_digest(4),
    // content_fingerprint(5), issued_by(6), issued_by_role(7), correlation_id(8), issued_at_utc(9),
    // schema_version(10), record_hash(11).
    private const string Columns =
        "tenant_id, project_id, policy_version, entries_canonical, policy_digest, content_fingerprint, issued_by, " +
        "issued_by_role, correlation_id, issued_at_utc, schema_version, record_hash";

    private const string LockedRecordsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_wdac_policy_evidence WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY policy_version DESC;
        """;

    private const string LatestSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.security_wdac_policy_evidence
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY policy_version DESC;
        """;

    private const string InsertSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @issuedAt);

        INSERT INTO dbo.security_wdac_policy_evidence ({Columns})
        VALUES
            (@tenant, @project, @version, @entriesCanonical, @policyDigest, @contentFingerprint, @issuedBy,
             @issuedByRole, @correlation, @issuedAt, @schemaVersion, @recordHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<WdacPolicyEvidence> RecordPolicyAsync(
        TenantScope scope,
        IReadOnlyList<WdacAllowlistEntry> entries,
        string issuedBy,
        string issuedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = WdacPolicyEvidence.Record(scope.Tenant, scope.Project, policyVersion: 1, entries, issuedBy, issuedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WdacPolicyEvidence? current = null;
            await using (var command = new SqlCommand(LockedRecordsSql, connection.Connection, transaction))
            {
                BindScope(command, scope);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    current = ReadRecord(reader);
                }
            }

            if (current is not null
                && string.Equals(current.ContentFingerprint.Value, candidate.ContentFingerprint.Value, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var nextVersion = (current?.PolicyVersion ?? 0) + 1;
            var record = WdacPolicyEvidence.Record(scope.Tenant, scope.Project, nextVersion, entries, issuedBy, issuedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertSql, connection.Connection, transaction))
            {
                BindRecordParameters(command, scope, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<WdacPolicyEvidence?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestSql, connection.Connection);
        BindScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    private static void BindScope(SqlCommand command, TenantScope scope)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindRecordParameters(SqlCommand command, TenantScope scope, WdacPolicyEvidence record)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.PolicyVersion });
        command.Parameters.Add(new SqlParameter("@entriesCanonical", SqlDbType.NVarChar, -1) { Value = EncodeEntries(record.Entries) });
        command.Parameters.Add(new SqlParameter("@policyDigest", SqlDbType.Char, 64) { Value = record.PolicyDigest.Value });
        command.Parameters.Add(new SqlParameter("@contentFingerprint", SqlDbType.Char, 64) { Value = record.ContentFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@issuedBy", SqlDbType.NVarChar, 200) { Value = record.IssuedBy });
        command.Parameters.Add(new SqlParameter("@issuedByRole", SqlDbType.NVarChar, 50) { Value = record.IssuedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@issuedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.IssuedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = record.RecordHash.Value });
    }

    private static WdacPolicyEvidence ReadRecord(SqlDataReader reader)
    {
        // Persistence is an untrusted boundary: a row whose entries_canonical column was tampered
        // with directly (bypassing the store) may not even be structurally parseable (e.g. missing
        // field/entry separators). Any such failure is an integrity violation, never a raw parsing
        // exception, and never a silent partial read.
        try
        {
            var tenant = new ArchiveBridge.Domain.IdentityAndAccess.TenantId(reader.GetGuid(0));
            var project = new ArchiveBridge.Domain.Projects.ProjectId(reader.GetGuid(1));
            var policyVersion = reader.GetInt32(2);
            var entries = DecodeEntries(reader.GetString(3));
            var policyDigest = new Sha256Hash(reader.GetString(4).TrimEnd());
            var contentFingerprint = new Sha256Hash(reader.GetString(5).TrimEnd());
            var issuedBy = reader.GetString(6).TrimEnd();
            var issuedByRole = reader.GetString(7).TrimEnd();
            var correlation = new CorrelationId(reader.GetGuid(8));
            var issuedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(9));
            var schemaVersion = reader.GetString(10).TrimEnd();
            var recordHash = new Sha256Hash(reader.GetString(11).TrimEnd());

            return WdacPolicyEvidence.Rehydrate(
                tenant, project, policyVersion, entries, policyDigest, issuedBy, issuedByRole, correlation, issuedAtUtc,
                schemaVersion, contentFingerprint, recordHash);
        }
        catch (Exception ex) when (ex is not WdacPolicyIntegrityViolationException)
        {
            throw new WdacPolicyIntegrityViolationException(
                "Falha ao reconstruir WdacPolicyEvidence a partir da linha persistida; conteúdo estruturalmente inválido ou adulterado.",
                ex);
        }
    }

    private static string EncodeEntries(IReadOnlyList<WdacAllowlistEntry> entries) =>
        string.Join(EntrySeparator, entries.Select(EncodeEntry));

    private static string EncodeEntry(WdacAllowlistEntry entry) =>
        string.Join(FieldSeparator, entry.Publisher ?? string.Empty, entry.Sha256?.Value ?? string.Empty, entry.PathRule ?? string.Empty);

    private static List<WdacAllowlistEntry> DecodeEntries(string canonical)
    {
        if (canonical.Length == 0)
        {
            return [];
        }

        var entries = new List<WdacAllowlistEntry>();
        foreach (var encodedEntry in canonical.Split(EntrySeparator))
        {
            var fields = encodedEntry.Split(FieldSeparator);
            var publisher = fields[0].Length == 0 ? null : fields[0];
            var hash = fields[1].Length == 0 ? (Sha256Hash?)null : new Sha256Hash(fields[1]);
            var pathRule = fields[2].Length == 0 ? null : fields[2];
            entries.Add(WdacAllowlistEntry.Create(publisher, hash, pathRule));
        }

        return entries;
    }
}
