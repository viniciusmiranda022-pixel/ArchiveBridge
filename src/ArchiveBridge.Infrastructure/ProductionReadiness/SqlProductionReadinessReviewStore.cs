using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.ProductionReadiness;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.ProductionReadiness;

/// <summary>
/// Persistência do <see cref="ProductionReadinessReviewSnapshot"/> (AB-I8-001) — um header por versão
/// (<c>production_readiness_review_snapshots</c>) mais uma linha por controle do catálogo dentro dessa
/// versão (<c>production_readiness_review_control_results</c>, mesmo padrão item-table de reconciliation
/// assessments). <see cref="RecordReviewAsync"/> locka, sob a MESMA transação, o header já existente deste
/// escopo e decide sob esse lock se o candidato converge para a versão vigente (mesmo
/// <see cref="ProductionReadinessReviewSnapshot.ReviewFingerprint"/>, replay idempotente) ou se é uma versão
/// realmente nova. Toda leitura revalida <see cref="ProductionReadinessReviewSnapshot.SnapshotHash"/> E
/// reexecuta o avaliador puro sobre as linhas de controle carregadas (fronteira não confiável). RLS por
/// SESSION_CONTEXT.
/// </summary>
public sealed class SqlProductionReadinessReviewStore(TenantConnectionFactory connectionFactory) : IProductionReadinessReviewStore
{
    // Colunas do header = tenant_id(0), project_id(1), review_version(2), build_commit_sha(3),
    // build_artifact_digest(4), policy_version_fingerprint(5), capability_matrix_fingerprint(6), outcome(7),
    // review_fingerprint(8), submitted_by(9), submitted_by_role(10), correlation_id(11),
    // generated_at_utc(12), schema_version(13), snapshot_hash(14).
    private const string HeaderColumns =
        "tenant_id, project_id, review_version, build_commit_sha, build_artifact_digest, " +
        "policy_version_fingerprint, capability_matrix_fingerprint, outcome, review_fingerprint, submitted_by, " +
        "submitted_by_role, correlation_id, generated_at_utc, schema_version, snapshot_hash";

    // Colunas das linhas de controle = tenant_id(0), project_id(1), review_version(2), control_id(3),
    // gate_group(4), status(5), evidence_kind(6), evidence_fingerprint(7), evidence_locator(8),
    // reason_code(9), observed_at_utc(10).
    private const string ControlColumns =
        "tenant_id, project_id, review_version, control_id, gate_group, status, evidence_kind, " +
        "evidence_fingerprint, evidence_locator, reason_code, observed_at_utc";

    private const string LockedHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.production_readiness_review_snapshots WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY review_version DESC;
        """;

    private const string LatestHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.production_readiness_review_snapshots
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY review_version DESC;
        """;

    private const string HistoryHeaderSql =
        $"""
        SELECT {HeaderColumns} FROM dbo.production_readiness_review_snapshots
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY review_version ASC;
        """;

    private const string ControlsForVersionSql =
        $"""
        SELECT {ControlColumns} FROM dbo.production_readiness_review_control_results
        WHERE tenant_id = @tenant AND project_id = @project AND review_version = @version
        ORDER BY control_id ASC;
        """;

    private const string InsertHeaderSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @generatedAt);

        INSERT INTO dbo.production_readiness_review_snapshots ({HeaderColumns})
        VALUES
            (@tenant, @project, @version, @commitSha, @artifactDigest, @policyFingerprint, @capabilityFingerprint,
             @outcome, @reviewFingerprint, @submittedBy, @submittedByRole, @correlation, @generatedAt,
             @schemaVersion, @snapshotHash);
        """;

    private const string InsertControlSql =
        $"""
        INSERT INTO dbo.production_readiness_review_control_results ({ControlColumns})
        VALUES
            (@tenant, @project, @version, @controlId, @gateGroup, @status, @evidenceKind, @evidenceFingerprint,
             @evidenceLocator, @reasonCode, @observedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<ProductionReadinessReviewSnapshot> RecordReviewAsync(
        TenantScope scope,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> resolvedControlResults,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Validação/normalização/avaliação PURA ANTES de abrir a transação — a versão 1 aqui é só um
        // placeholder para computar o fingerprint; a versão REAL é alocada sob lock abaixo (mesma técnica de
        // SqlReconciliationCertificateStore/SqlRecoveryReadinessStore).
        var candidate = ProductionReadinessReviewSnapshot.Compose(
            scope.Tenant, scope.Project, reviewVersion: 1, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
            capabilityMatrixFingerprint, resolvedControlResults, submittedBy, submittedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProductionReadinessReviewSnapshot? current = null;
            HeaderRow? currentHeader = null;
            await using (var command = new SqlCommand(LockedHeaderSql, connection.Connection, transaction))
            {
                BindScope(command, scope);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    currentHeader = ReadHeader(reader);
                }
            }

            if (currentHeader is { } header)
            {
                var controls = await ReadControlsAsync(connection.Connection, transaction, scope, header.ReviewVersion, cancellationToken)
                    .ConfigureAwait(false);
                current = RehydrateSnapshot(header, controls);
            }

            if (current is not null
                && string.Equals(current.ReviewFingerprint.Value, candidate.ReviewFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico: converge sem inserir uma nova versão, mesmo sob concorrência — composições
                // concorrentes idênticas convergem todas para a MESMA versão vigente.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var nextVersion = (current?.ReviewVersion ?? 0) + 1;
            var record = ProductionReadinessReviewSnapshot.Compose(
                scope.Tenant, scope.Project, nextVersion, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
                capabilityMatrixFingerprint, resolvedControlResults, submittedBy, submittedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertHeaderSql, connection.Connection, transaction))
            {
                BindHeaderParameters(command, scope, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var controlResult in record.ControlResults)
            {
                await using var command = new SqlCommand(InsertControlSql, connection.Connection, transaction);
                BindControlParameters(command, scope, record.ReviewVersion, controlResult);
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
    public async Task<ProductionReadinessReviewSnapshot?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        HeaderRow? header;
        await using (var command = new SqlCommand(LatestHeaderSql, connection.Connection))
        {
            BindScope(command, scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            header = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadHeader(reader) : null;
        }

        if (header is not { } value)
        {
            return null;
        }

        var controls = await ReadControlsAsync(connection.Connection, transaction: null, scope, value.ReviewVersion, cancellationToken)
            .ConfigureAwait(false);
        return RehydrateSnapshot(value, controls);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductionReadinessReviewSnapshot>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        var headers = new List<HeaderRow>();
        await using (var command = new SqlCommand(HistoryHeaderSql, connection.Connection))
        {
            BindScope(command, scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                headers.Add(ReadHeader(reader));
            }
        }

        var history = new List<ProductionReadinessReviewSnapshot>(headers.Count);
        foreach (var header in headers)
        {
            var controls = await ReadControlsAsync(connection.Connection, transaction: null, scope, header.ReviewVersion, cancellationToken)
                .ConfigureAwait(false);
            history.Add(RehydrateSnapshot(header, controls));
        }

        return history;
    }

    private static async Task<List<ReadinessControlResult>> ReadControlsAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, int reviewVersion, CancellationToken cancellationToken)
    {
        var controls = new List<ReadinessControlResult>();
        await using var command = transaction is null
            ? new SqlCommand(ControlsForVersionSql, connection)
            : new SqlCommand(ControlsForVersionSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reviewVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            controls.Add(ReadControlResult(reader));
        }

        return controls;
    }

    private static ProductionReadinessReviewSnapshot RehydrateSnapshot(HeaderRow header, IReadOnlyList<ReadinessControlResult> controls)
    {
        var blockers = controls
            .Where(result => result.Status != ReadinessControlStatus.Pass)
            .Select(result => new ProductionReadinessBlocker(
                result.ControlId, ReadinessControlCatalog.Definition(result.ControlId).Group, result.Status, result.ReasonCode))
            .ToList();

        return ProductionReadinessReviewSnapshot.Rehydrate(
            header.Tenant, header.Project, header.ReviewVersion, header.BuildCommitSha, header.BuildArtifactDigest,
            header.PolicyVersionFingerprint, header.CapabilityMatrixFingerprint, controls, header.Outcome, blockers,
            header.ReviewFingerprint, header.SubmittedBy, header.SubmittedByRole, header.Correlation, header.GeneratedAtUtc,
            header.SchemaVersion, header.SnapshotHash);
    }

    private static void BindScope(SqlCommand command, TenantScope scope)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindHeaderParameters(SqlCommand command, TenantScope scope, ProductionReadinessReviewSnapshot record)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.ReviewVersion });
        command.Parameters.Add(new SqlParameter("@commitSha", SqlDbType.Char, 40) { Value = record.BuildCommitSha });
        command.Parameters.Add(new SqlParameter("@artifactDigest", SqlDbType.Char, 64) { Value = record.BuildArtifactDigest.Value });
        command.Parameters.Add(new SqlParameter("@policyFingerprint", SqlDbType.Char, 64) { Value = record.PolicyVersionFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@capabilityFingerprint", SqlDbType.Char, 64) { Value = record.CapabilityMatrixFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@reviewFingerprint", SqlDbType.Char, 64) { Value = record.ReviewFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@submittedBy", SqlDbType.NVarChar, 200) { Value = record.SubmittedBy });
        command.Parameters.Add(new SqlParameter("@submittedByRole", SqlDbType.NVarChar, 50) { Value = record.SubmittedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@generatedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.GeneratedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@snapshotHash", SqlDbType.Char, 64) { Value = record.SnapshotHash.Value });
    }

    private static void BindControlParameters(SqlCommand command, TenantScope scope, int reviewVersion, ReadinessControlResult result)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reviewVersion });
        command.Parameters.Add(new SqlParameter("@controlId", SqlDbType.NVarChar, 80) { Value = result.ControlId.Value });
        command.Parameters.Add(new SqlParameter("@gateGroup", SqlDbType.TinyInt) { Value = (byte)result.Group });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)result.Status });
        command.Parameters.Add(new SqlParameter("@evidenceKind", SqlDbType.TinyInt) { Value = (byte)result.Evidence.Kind });
        command.Parameters.Add(new SqlParameter("@evidenceFingerprint", SqlDbType.Char, 64) { Value = result.Evidence.Fingerprint.Value });
        command.Parameters.Add(new SqlParameter("@evidenceLocator", SqlDbType.NVarChar, 300) { Value = result.Evidence.Locator });
        command.Parameters.Add(new SqlParameter("@reasonCode", SqlDbType.NVarChar, 200) { Value = result.ReasonCode });
        command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(result.ObservedAtUtc) });
    }

    private static HeaderRow ReadHeader(SqlDataReader reader) =>
        new(
            new TenantId(reader.GetGuid(0)),
            new ProjectId(reader.GetGuid(1)),
            reader.GetInt32(2),
            reader.GetString(3).TrimEnd(),
            new Sha256Hash(reader.GetString(4).TrimEnd()),
            new Sha256Hash(reader.GetString(5).TrimEnd()),
            new Sha256Hash(reader.GetString(6).TrimEnd()),
            (ProductionReadinessOutcome)reader.GetByte(7),
            new Sha256Hash(reader.GetString(8).TrimEnd()),
            reader.GetString(9).TrimEnd(),
            reader.GetString(10).TrimEnd(),
            new CorrelationId(reader.GetGuid(11)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
            reader.GetString(13).TrimEnd(),
            new Sha256Hash(reader.GetString(14).TrimEnd()));

    private static ReadinessControlResult ReadControlResult(SqlDataReader reader)
    {
        var controlId = new ReadinessControlId(reader.GetString(3).TrimEnd());
        var group = (ReadinessGateGroup)reader.GetByte(4);
        var status = (ReadinessControlStatus)reader.GetByte(5);
        var evidenceKind = (ReadinessEvidenceKind)reader.GetByte(6);
        var evidenceFingerprint = new Sha256Hash(reader.GetString(7).TrimEnd());
        var evidenceLocator = reader.GetString(8).TrimEnd();
        var reasonCode = reader.GetString(9).TrimEnd();
        var observedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(10));

        var evidence = ReadinessEvidenceReference.Rehydrate(evidenceKind, evidenceFingerprint, evidenceLocator);
        return ReadinessControlResult.Create(controlId, group, status, evidence, reasonCode, observedAtUtc);
    }

    private sealed record HeaderRow(
        TenantId Tenant,
        ProjectId Project,
        int ReviewVersion,
        string BuildCommitSha,
        Sha256Hash BuildArtifactDigest,
        Sha256Hash PolicyVersionFingerprint,
        Sha256Hash CapabilityMatrixFingerprint,
        ProductionReadinessOutcome Outcome,
        Sha256Hash ReviewFingerprint,
        string SubmittedBy,
        string SubmittedByRole,
        CorrelationId Correlation,
        DateTimeOffset GeneratedAtUtc,
        string SchemaVersion,
        Sha256Hash SnapshotHash);
}
