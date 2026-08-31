using System.Data;
using ArchiveBridge.Contracts.GoLive;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.GoLive;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.ProductionReadiness;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.GoLive;

/// <summary>
/// Persistência da <see cref="GoLiveAuthorizationDecision"/> (AB-I8-010) — um header por versão
/// (<c>go_live_authorizations</c>) mais uma linha por controle operacional/M365 revalidado dentro dessa versão
/// (<c>go_live_authorization_operational_control_results</c>, mesmo padrão item-table de
/// <c>production_readiness_review_control_results</c>/0042). <see cref="AuthorizeAsync"/> locka, sob a MESMA
/// transação, o header já existente deste escopo e decide sob esse lock se o candidato converge para a versão
/// vigente (mesmo <see cref="GoLiveAuthorizationDecision.AuthorizationFingerprint"/>, replay idempotente) ou
/// se é uma versão realmente nova — nesse caso reaproveita o <see cref="GoLiveAuthorizationId"/> já existente
/// (identidade estável da decisão). Toda leitura revalida <see cref="GoLiveAuthorizationDecision.AuthorizationHash"/>
/// (fronteira não confiável). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlGoLiveAuthorizationStore(TenantConnectionFactory connectionFactory) : IGoLiveAuthorizationStore
{
    // Colunas do header = tenant_id(0), project_id(1), authorization_version(2), authorization_id(3),
    // canary_plan_id(4), canary_plan_version(5), canary_plan_fingerprint(6), readiness_review_version(7),
    // readiness_review_fingerprint(8), build_commit_sha(9), build_artifact_digest(10),
    // policy_version_fingerprint(11), capability_matrix_fingerprint(12), canary_outcome_at_authorization(13),
    // current_readiness_review_version_at_authorization(14), current_readiness_review_fingerprint_at_authorization(15),
    // outcome(16), authorization_fingerprint(17), authorized_by(18), authorized_by_role(19), correlation_id(20),
    // authorized_at_utc(21), schema_version(22), authorization_hash(23).
    private const string HeaderColumns =
        "tenant_id, project_id, authorization_version, authorization_id, canary_plan_id, canary_plan_version, " +
        "canary_plan_fingerprint, readiness_review_version, readiness_review_fingerprint, build_commit_sha, " +
        "build_artifact_digest, policy_version_fingerprint, capability_matrix_fingerprint, canary_outcome_at_authorization, " +
        "current_readiness_review_version_at_authorization, current_readiness_review_fingerprint_at_authorization, outcome, " +
        "authorization_fingerprint, authorized_by, authorized_by_role, correlation_id, authorized_at_utc, schema_version, " +
        "authorization_hash";

    // Colunas das linhas de controle = tenant_id(0), project_id(1), authorization_version(2), control_id(3),
    // gate_group(4), status(5), evidence_kind(6), evidence_fingerprint(7), evidence_locator(8), reason_code(9),
    // observed_at_utc(10).
    private const string ControlColumns =
        "tenant_id, project_id, authorization_version, control_id, gate_group, status, evidence_kind, " +
        "evidence_fingerprint, evidence_locator, reason_code, observed_at_utc";

    private const string LockedHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.go_live_authorizations WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY authorization_version DESC;
        """;

    private const string LatestHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.go_live_authorizations
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY authorization_version DESC;
        """;

    private const string VersionHeaderSql =
        $"""
        SELECT TOP (1) {HeaderColumns} FROM dbo.go_live_authorizations
        WHERE tenant_id = @tenant AND project_id = @project AND authorization_version = @version;
        """;

    private const string HistoryHeaderSql =
        $"""
        SELECT {HeaderColumns} FROM dbo.go_live_authorizations
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY authorization_version ASC;
        """;

    private const string ControlsForVersionSql =
        $"""
        SELECT {ControlColumns} FROM dbo.go_live_authorization_operational_control_results
        WHERE tenant_id = @tenant AND project_id = @project AND authorization_version = @version
        ORDER BY control_id ASC;
        """;

    private const string InsertHeaderSql =
        $"""
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE tenant_id = @tenant AND project_id = @project)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@project, @tenant, @authorizedAt);

        INSERT INTO dbo.go_live_authorizations ({HeaderColumns})
        VALUES
            (@tenant, @project, @version, @authorizationId, @canaryPlanId, @canaryPlanVersion, @canaryPlanFingerprint,
             @readinessVersion, @readinessFingerprint, @commitSha, @artifactDigest, @policyFingerprint, @capabilityFingerprint,
             @canaryOutcome, @currentReadinessVersion, @currentReadinessFingerprint, @outcome, @authorizationFingerprint,
             @authorizedBy, @authorizedByRole, @correlation, @authorizedAt, @schemaVersion, @authorizationHash);
        """;

    private const string InsertControlSql =
        $"""
        INSERT INTO dbo.go_live_authorization_operational_control_results ({ControlColumns})
        VALUES
            (@tenant, @project, @version, @controlId, @gateGroup, @status, @evidenceKind, @evidenceFingerprint,
             @evidenceLocator, @reasonCode, @observedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<GoLiveAuthorizationDecision> AuthorizeAsync(
        TenantScope scope,
        CanaryPlanId canaryPlanId,
        int canaryPlanVersion,
        Sha256Hash canaryPlanFingerprint,
        int readinessReviewVersion,
        Sha256Hash readinessReviewFingerprint,
        string buildCommitSha,
        Sha256Hash buildArtifactDigest,
        Sha256Hash policyVersionFingerprint,
        Sha256Hash capabilityMatrixFingerprint,
        CanaryOutcome canaryOutcomeAtAuthorization,
        int? currentReadinessReviewVersionAtAuthorization,
        Sha256Hash? currentReadinessReviewFingerprintAtAuthorization,
        IReadOnlyDictionary<ReadinessControlId, ReadinessControlResult> operationalResolvedResults,
        string authorizedBy,
        string authorizedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Validação/normalização/avaliação PURA ANTES de abrir a transação — placeholders de identidade/versão
        // apenas para computar o fingerprint (mesma técnica de SqlCanaryPlanStore).
        var candidate = GoLiveAuthorizationDecision.Compose(
            scope.Tenant, scope.Project, GoLiveAuthorizationId.New(), authorizationVersion: 1, canaryPlanId, canaryPlanVersion,
            canaryPlanFingerprint, readinessReviewVersion, readinessReviewFingerprint, buildCommitSha, buildArtifactDigest,
            policyVersionFingerprint, capabilityMatrixFingerprint, canaryOutcomeAtAuthorization,
            currentReadinessReviewVersionAtAuthorization, currentReadinessReviewFingerprintAtAuthorization,
            operationalResolvedResults, authorizedBy, authorizedByRole, correlation, now);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GoLiveAuthorizationDecision? current = null;
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
                var controls = await ReadControlsAsync(connection.Connection, transaction, scope, header.AuthorizationVersion, cancellationToken)
                    .ConfigureAwait(false);
                current = RehydrateDecision(header, controls);
            }

            if (current is not null
                && string.Equals(current.AuthorizationFingerprint.Value, candidate.AuthorizationFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico: converge sem inserir uma nova versão, mesmo sob concorrência — decisões
                // concorrentes idênticas convergem todas para a MESMA versão vigente (nunca duas autorizações
                // canônicas conflitantes, escopo obrigatório item 5).
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var authorizationId = current?.AuthorizationId ?? GoLiveAuthorizationId.New();
            var nextVersion = (current?.AuthorizationVersion ?? 0) + 1;
            var record = GoLiveAuthorizationDecision.Compose(
                scope.Tenant, scope.Project, authorizationId, nextVersion, canaryPlanId, canaryPlanVersion, canaryPlanFingerprint,
                readinessReviewVersion, readinessReviewFingerprint, buildCommitSha, buildArtifactDigest, policyVersionFingerprint,
                capabilityMatrixFingerprint, canaryOutcomeAtAuthorization, currentReadinessReviewVersionAtAuthorization,
                currentReadinessReviewFingerprintAtAuthorization, operationalResolvedResults, authorizedBy, authorizedByRole,
                correlation, now);

            await using (var command = new SqlCommand(InsertHeaderSql, connection.Connection, transaction))
            {
                BindHeaderParameters(command, scope, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var controlResult in record.OperationalControlResults)
            {
                await using var command = new SqlCommand(InsertControlSql, connection.Connection, transaction);
                BindControlParameters(command, scope, record.AuthorizationVersion, controlResult);
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
    public async Task<GoLiveAuthorizationDecision?> GetLatestAsync(TenantScope scope, CancellationToken cancellationToken)
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

        var controls = await ReadControlsAsync(connection.Connection, transaction: null, scope, value.AuthorizationVersion, cancellationToken)
            .ConfigureAwait(false);
        return RehydrateDecision(value, controls);
    }

    /// <inheritdoc />
    public async Task<GoLiveAuthorizationDecision?> GetByVersionAsync(TenantScope scope, int authorizationVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        HeaderRow? header;
        await using (var command = new SqlCommand(VersionHeaderSql, connection.Connection))
        {
            BindScope(command, scope);
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = authorizationVersion });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            header = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadHeader(reader) : null;
        }

        if (header is not { } value)
        {
            return null;
        }

        var controls = await ReadControlsAsync(connection.Connection, transaction: null, scope, value.AuthorizationVersion, cancellationToken)
            .ConfigureAwait(false);
        return RehydrateDecision(value, controls);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoLiveAuthorizationDecision>> GetHistoryAsync(TenantScope scope, CancellationToken cancellationToken)
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

        var history = new List<GoLiveAuthorizationDecision>(headers.Count);
        foreach (var header in headers)
        {
            var controls = await ReadControlsAsync(connection.Connection, transaction: null, scope, header.AuthorizationVersion, cancellationToken)
                .ConfigureAwait(false);
            history.Add(RehydrateDecision(header, controls));
        }

        return history;
    }

    private static async Task<List<ReadinessControlResult>> ReadControlsAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, int authorizationVersion, CancellationToken cancellationToken)
    {
        var controls = new List<ReadinessControlResult>();
        await using var command = transaction is null
            ? new SqlCommand(ControlsForVersionSql, connection)
            : new SqlCommand(ControlsForVersionSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = authorizationVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            controls.Add(ReadControlResult(reader));
        }

        return controls;
    }

    private static GoLiveAuthorizationDecision RehydrateDecision(HeaderRow header, IReadOnlyList<ReadinessControlResult> controls) =>
        GoLiveAuthorizationDecision.Rehydrate(
            header.Tenant, header.Project, header.AuthorizationId, header.AuthorizationVersion, header.CanaryPlanId,
            header.CanaryPlanVersion, header.CanaryPlanFingerprint, header.ReadinessReviewVersion, header.ReadinessReviewFingerprint,
            header.BuildCommitSha, header.BuildArtifactDigest, header.PolicyVersionFingerprint, header.CapabilityMatrixFingerprint,
            header.CanaryOutcomeAtAuthorization, header.CurrentReadinessReviewVersionAtAuthorization,
            header.CurrentReadinessReviewFingerprintAtAuthorization, controls, header.Outcome, header.AuthorizationFingerprint,
            header.AuthorizedBy, header.AuthorizedByRole, header.Correlation, header.AuthorizedAtUtc, header.SchemaVersion,
            header.AuthorizationHash);

    private static void BindScope(SqlCommand command, TenantScope scope)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static void BindHeaderParameters(SqlCommand command, TenantScope scope, GoLiveAuthorizationDecision record)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.AuthorizationVersion });
        command.Parameters.Add(new SqlParameter("@authorizationId", SqlDbType.UniqueIdentifier) { Value = record.AuthorizationId.Value });
        command.Parameters.Add(new SqlParameter("@canaryPlanId", SqlDbType.UniqueIdentifier) { Value = record.CanaryPlanId.Value });
        command.Parameters.Add(new SqlParameter("@canaryPlanVersion", SqlDbType.Int) { Value = record.CanaryPlanVersion });
        command.Parameters.Add(new SqlParameter("@canaryPlanFingerprint", SqlDbType.Char, 64) { Value = record.CanaryPlanFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@readinessVersion", SqlDbType.Int) { Value = record.ReadinessReviewVersion });
        command.Parameters.Add(new SqlParameter("@readinessFingerprint", SqlDbType.Char, 64) { Value = record.ReadinessReviewFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@commitSha", SqlDbType.Char, 40) { Value = record.BuildCommitSha });
        command.Parameters.Add(new SqlParameter("@artifactDigest", SqlDbType.Char, 64) { Value = record.BuildArtifactDigest.Value });
        command.Parameters.Add(new SqlParameter("@policyFingerprint", SqlDbType.Char, 64) { Value = record.PolicyVersionFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@capabilityFingerprint", SqlDbType.Char, 64) { Value = record.CapabilityMatrixFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@canaryOutcome", SqlDbType.TinyInt) { Value = (byte)record.CanaryOutcomeAtAuthorization });
        command.Parameters.Add(new SqlParameter("@currentReadinessVersion", SqlDbType.Int)
        {
            Value = (object?)record.CurrentReadinessReviewVersionAtAuthorization ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@currentReadinessFingerprint", SqlDbType.Char, 64)
        {
            Value = (object?)record.CurrentReadinessReviewFingerprintAtAuthorization?.Value ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)record.Outcome });
        command.Parameters.Add(new SqlParameter("@authorizationFingerprint", SqlDbType.Char, 64) { Value = record.AuthorizationFingerprint.Value });
        command.Parameters.Add(new SqlParameter("@authorizedBy", SqlDbType.NVarChar, 200) { Value = record.AuthorizedBy });
        command.Parameters.Add(new SqlParameter("@authorizedByRole", SqlDbType.NVarChar, 50) { Value = record.AuthorizedByRole });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = record.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@authorizedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(record.AuthorizedAtUtc) });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = record.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@authorizationHash", SqlDbType.Char, 64) { Value = record.AuthorizationHash.Value });
    }

    private static void BindControlParameters(SqlCommand command, TenantScope scope, int authorizationVersion, ReadinessControlResult result)
    {
        BindScope(command, scope);
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = authorizationVersion });
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
            new GoLiveAuthorizationId(reader.GetGuid(3)),
            new CanaryPlanId(reader.GetGuid(4)),
            reader.GetInt32(5),
            new Sha256Hash(reader.GetString(6).TrimEnd()),
            reader.GetInt32(7),
            new Sha256Hash(reader.GetString(8).TrimEnd()),
            reader.GetString(9).TrimEnd(),
            new Sha256Hash(reader.GetString(10).TrimEnd()),
            new Sha256Hash(reader.GetString(11).TrimEnd()),
            new Sha256Hash(reader.GetString(12).TrimEnd()),
            (CanaryOutcome)reader.GetByte(13),
            reader.IsDBNull(14) ? null : reader.GetInt32(14),
            reader.IsDBNull(15) ? null : new Sha256Hash(reader.GetString(15).TrimEnd()),
            (GoLiveOutcome)reader.GetByte(16),
            new Sha256Hash(reader.GetString(17).TrimEnd()),
            reader.GetString(18).TrimEnd(),
            reader.GetString(19).TrimEnd(),
            new CorrelationId(reader.GetGuid(20)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(21)),
            reader.GetString(22).TrimEnd(),
            new Sha256Hash(reader.GetString(23).TrimEnd()));

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
        int AuthorizationVersion,
        GoLiveAuthorizationId AuthorizationId,
        CanaryPlanId CanaryPlanId,
        int CanaryPlanVersion,
        Sha256Hash CanaryPlanFingerprint,
        int ReadinessReviewVersion,
        Sha256Hash ReadinessReviewFingerprint,
        string BuildCommitSha,
        Sha256Hash BuildArtifactDigest,
        Sha256Hash PolicyVersionFingerprint,
        Sha256Hash CapabilityMatrixFingerprint,
        CanaryOutcome CanaryOutcomeAtAuthorization,
        int? CurrentReadinessReviewVersionAtAuthorization,
        Sha256Hash? CurrentReadinessReviewFingerprintAtAuthorization,
        GoLiveOutcome Outcome,
        Sha256Hash AuthorizationFingerprint,
        string AuthorizedBy,
        string AuthorizedByRole,
        CorrelationId Correlation,
        DateTimeOffset AuthorizedAtUtc,
        string SchemaVersion,
        Sha256Hash AuthorizationHash);
}
