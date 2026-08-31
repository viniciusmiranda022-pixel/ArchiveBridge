using System.Data;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Canary;

/// <summary>
/// Persistência do <see cref="CanaryPlan"/> (AB-I8-004) — um header por versão
/// (<c>canary_plans</c>). <see cref="AuthorizeAsync"/> locka, sob a MESMA transação, o header já existente
/// deste escopo e decide sob esse lock se o candidato converge para a versão vigente (mesmo
/// <see cref="CanaryPlan.PlanFingerprint"/>, replay idempotente) ou se é uma versão realmente nova — nesse
/// caso reaproveita o <see cref="CanaryPlan.PlanId"/> já existente (identidade estável do plano). Toda
/// leitura revalida <see cref="CanaryPlan.PlanHash"/> (fronteira não confiável). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlCanaryPlanStore(TenantConnectionFactory connectionFactory) : ICanaryPlanStore
{
    // Colunas = tenant_id(0), project_id(1), plan_version(2), plan_id(3), readiness_review_version(4),
    // readiness_review_fingerprint(5), build_commit_sha(6), build_artifact_digest(7),
    // policy_version_fingerprint(8), capability_matrix_fingerprint(9), plan_fingerprint(10),
    // authorized_by(11), authorized_by_role(12), correlation_id(13), authorized_at_utc(14),
    // schema_version(15), plan_hash(16).
    private const string Columns =
        "tenant_id, project_id, plan_version, plan_id, readiness_review_version, readiness_review_fingerprint, " +
        "build_commit_sha, build_artifact_digest, policy_version_fingerprint, capability_matrix_fingerprint, " +
        "plan_fingerprint, authorized_by, authorized_by_role, correlation_id, authorized_at_utc, schema_version, plan_hash";

    private const string LockedHeaderSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.canary_plans WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY plan_version DESC;
        """;

    private const string LatestHeaderSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.canary_plans
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY plan_version DESC;
        """;

    private const string VersionHeaderSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.canary_plans
        WHERE tenant_id = @tenant AND project_id = @project AND plan_version = @version;
        """;

    private const string HistoryHeaderSql =
        $"""
        SELECT {Columns} FROM dbo.canary_plans
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY plan_version ASC;
        """;

    private const string InsertHeaderSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @authorizedAt);

        INSERT INTO dbo.canary_plans ({Columns})
        VALUES
            (@tenant, @project, @version, @planId, @readinessVersion, @readinessFingerprint, @commitSha, @artifactDigest,
             @policyFingerprint, @capabilityFingerprint, @planFingerprint, @authorizedBy, @authorizedByRole, @correlation,
             @authorizedAt, @schemaVersion, @planHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<CanaryPlan> AuthorizeAsync(
        TenantScope scope,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        ProductionReadinessOutcome readinessOutcome,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Validação/normalização/gate de entrada PURA ANTES de abrir a transação — planId/versão 1 aqui são
        // só placeholders para computar o fingerprint (PlanFingerprint nunca cobre planId/versão); a
        // identidade/versão REAIS são resolvidas sob lock abaixo (mesma técnica de
        // SqlProductionReadinessReviewStore).
        var candidate = CanaryPlan.Compose(
            scope.Tenant, scope.Project, CanaryPlanId.New(), planVersion: 1, readinessReviewVersion, readinessReviewFingerprint,
            readinessOutcome, buildCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint,
            authorizedBy, authorizedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CanaryPlan? current = null;
            await using (var command = new SqlCommand(LockedHeaderSql, connection.Connection, transaction))
            {
                BindScope(command, scope);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    current = RehydrateFromReader(reader);
                }
            }

            if (current is not null && string.Equals(current.PlanFingerprint.Value, candidate.PlanFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico: converge sem inserir uma nova versão, mesmo sob concorrência —
                // autorizações concorrentes idênticas convergem todas para a MESMA versão vigente.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var planId = current?.PlanId ?? CanaryPlanId.New();
            var nextVersion = (current?.PlanVersion ?? 0) + 1;
            var record = CanaryPlan.Compose(
                scope.Tenant, scope.Project, planId, nextVersion, readinessReviewVersion, readinessReviewFingerprint,
                readinessOutcome, buildCommitSha, buildArtifactDigest, policyVersionFingerprint, capabilityMatrixFingerprint,
                authorizedBy, authorizedByRole, correlation, now);

            await using (var command = new SqlCommand(InsertHeaderSql, connection.Connection, transaction))
            {
                BindHeaderParameters(command, scope, record);
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
    public async Task<CanaryPlan?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestHeaderSql, connection.Connection);
        BindScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? RehydrateFromReader(reader) : null;
    }

    /// <inheritdoc />
    public async Task<CanaryPlan?> GetByVersionAsync(TenantScope scope, int planVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(VersionHeaderSql, connection.Connection);
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = planVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? RehydrateFromReader(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CanaryPlan>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var history = new List<CanaryPlan>();
        await using var command = new SqlCommand(HistoryHeaderSql, connection.Connection);
        BindScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(RehydrateFromReader(reader));
        }

        return history;
    }

    private static void BindScope(SqlCommand command, TenantScope scope)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindHeaderParameters(SqlCommand command, TenantScope scope, CanaryPlan record)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.PlanVersion });
        command.Parameters.Add(new SqlParameter("@planId", SqlDbType.UniqueIdentifier) { Value = record.PlanId.Value });
        command.Parameters.Add(new SqlParameter("@readinessVersion", SqlDbType.Int) { Value = record.ReadinessReviewVersion });
        command.Parameters.Add(new SqlParameter("@readinessFingerprint", SqlDbType.Char, 64) { Value = record.ReadinessReviewFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@commitSha", SqlDbType.Char, 40) { Value = record.BuildCommitSha });
        command.Parameters.Add(new SqlParameter("@artifactDigest", SqlDbType.Char, 64) { Value = record.BuildArtifactDigest.Value });
        command.Parameters.Add(new SqlParameter("@policyFingerprint", SqlDbType.Char, 64) { Value = record.PolicyVersionFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@capabilityFingerprint", SqlDbType.Char, 64) { Value = record.CapabilityMatrixFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@planFingerprint", SqlDbType.Char, 64) { Value = record.PlanFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@authorizedBy", SqlDbType.NVarChar, 200) { Value = record.AuthorizedBy });
        command.Parameters.Add(new SqlParameter("@authorizedByRole", SqlDbType.NVarChar, 50) { Value = record.AuthorizedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@authorizedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.AuthorizedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@planHash", SqlDbType.Char, 64) { Value = record.PlanHash.Value });
    }

    private static CanaryPlan RehydrateFromReader(SqlDataReader reader)
    {
        var tenant = new TenantId(reader.GetGuid(0));
        var project = new ProjectId(reader.GetGuid(1));
        var planVersion = reader.GetInt32(2);
        var planId = new CanaryPlanId(reader.GetGuid(3));
        var readinessVersion = reader.GetInt32(4);
        var readinessFingerprint = new Sha256Hash(reader.GetString(5).TrimEnd());
        var commitSha = reader.GetString(6).TrimEnd();
        var artifactDigest = new Sha256Hash(reader.GetString(7).TrimEnd());
        var policyFingerprint = new Sha256Hash(reader.GetString(8).TrimEnd());
        var capabilityFingerprint = new Sha256Hash(reader.GetString(9).TrimEnd());
        var planFingerprint = new Sha256Hash(reader.GetString(10).TrimEnd());
        var authorizedBy = reader.GetString(11).TrimEnd();
        var authorizedByRole = reader.GetString(12).TrimEnd();
        var correlation = new CorrelationId(reader.GetGuid(13));
        var authorizedAtUtc = SqlJobMapping.ReadUtc(reader.GetDateTime(14));
        var schemaVersion = reader.GetString(15).TrimEnd();
        var planHash = new Sha256Hash(reader.GetString(16).TrimEnd());

        return CanaryPlan.Rehydrate(
            tenant, project, planId, planVersion, readinessVersion, readinessFingerprint, commitSha, artifactDigest,
            policyFingerprint, capabilityFingerprint, planFingerprint, authorizedBy, authorizedByRole, correlation,
            authorizedAtUtc, schemaVersion, planHash);
    }
}
