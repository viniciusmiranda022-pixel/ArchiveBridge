using System.Data;
using System.Globalization;
using ArchiveBridge.Contracts.Canary;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Canary;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Canary;

/// <summary>
/// Persistência dos <see cref="CanaryScenarioResult"/> submetidos ao longo do tempo (AB-I8-004) — uma linha
/// append-only por submissão (<c>canary_scenario_results</c>), escopada a UMA versão específica do plano.
/// <see cref="RecordResultAsync"/> primeiro revalida, SOB A MESMA TRANSAÇÃO, que <paramref
/// name="planVersion"/> (do método) ainda é a versão VIGENTE do plano deste escopo — uma submissão contra
/// uma versão superada é recusada fail-closed ANTES de qualquer INSERT (<see cref="CanaryPlanSupersededException"/>,
/// escopo obrigatório item 5) — depois locka os resultados já existentes deste cenário/versão e decide sob
/// esse lock se o candidato converge (mesmo conteúdo, replay idempotente) ou se é uma versão de resultado
/// realmente nova. Toda leitura revalida <see cref="CanaryScenarioResult.ComputeContentFingerprint"/>/
/// <see cref="CanaryScenarioResult.ComputeRecordHash"/> contra os campos REALMENTE persistidos (fronteira não
/// confiável). RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlCanaryScenarioResultStore(TenantConnectionFactory connectionFactory) : ICanaryScenarioResultStore
{
    private const string CurrentSchemaVersion = "archivebridge.canary.scenario-result.v1";

    // Colunas = tenant_id(0), project_id(1), plan_version(2), scenario_id(3), result_version(4), status(5),
    // evidence_kind(6), evidence_fingerprint(7), evidence_locator(8), reason_code(9), observed_at_utc(10),
    // submitted_by(11), submitted_by_role(12), correlation_id(13), recorded_at_utc(14), schema_version(15),
    // content_fingerprint(16), record_hash(17).
    private const string Columns =
        "tenant_id, project_id, plan_version, scenario_id, result_version, status, evidence_kind, evidence_fingerprint, " +
        "evidence_locator, reason_code, observed_at_utc, submitted_by, submitted_by_role, correlation_id, " +
        "recorded_at_utc, schema_version, content_fingerprint, record_hash";

    private const string LatestPlanVersionSql =
        """
        SELECT TOP (1) plan_version FROM dbo.canary_plans WITH (HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project
        ORDER BY plan_version DESC;
        """;

    private const string LockedResultsSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.canary_scenario_results WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND plan_version = @planVersion AND scenario_id = @scenarioId
        ORDER BY result_version DESC;
        """;

    private const string LatestResultSql =
        $"""
        SELECT TOP (1) {Columns} FROM dbo.canary_scenario_results
        WHERE tenant_id = @tenant AND project_id = @project AND plan_version = @planVersion AND scenario_id = @scenarioId
        ORDER BY result_version DESC;
        """;

    private const string LatestForAllSql =
        $"""
        SELECT {Columns} FROM
        (
            SELECT {Columns}, ROW_NUMBER() OVER (PARTITION BY scenario_id ORDER BY result_version DESC) AS rn
            FROM dbo.canary_scenario_results
            WHERE tenant_id = @tenant AND project_id = @project AND plan_version = @planVersion
        ) ranked
        WHERE rn = 1
        ORDER BY scenario_id ASC;
        """;

    private const string HistorySql =
        $"""
        SELECT {Columns} FROM dbo.canary_scenario_results
        WHERE tenant_id = @tenant AND project_id = @project AND plan_version = @planVersion AND scenario_id = @scenarioId
        ORDER BY result_version ASC;
        """;

    private const string InsertSql =
        $"""
        INSERT INTO dbo.canary_scenario_results ({Columns})
        VALUES
            (@tenant, @project, @planVersion, @scenarioId, @resultVersion, @status, @evidenceKind, @evidenceFingerprint,
             @evidenceLocator, @reasonCode, @observedAt, @submittedBy, @submittedByRole, @correlation, @recordedAt,
             @schemaVersion, @contentFingerprint, @recordHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<CanaryScenarioResult> RecordResultAsync(
        TenantScope scope,
        int planVersion,
        CanaryScenarioId scenarioId,
        CanaryScenarioStatus status,
        CanaryEvidenceReference evidence,
        string reasonCode,
        DateTimeOffset observedAtUtc,
        string submittedBy,
        string submittedByRole,
        CorrelationId correlation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Truncar para milissegundos ANTES de qualquer fingerprint/hash (mesma técnica de
        // CanaryPlan.Compose/ProductionReadinessReviewSnapshot.Compose): a coluna DATETIME2(3) só armazena
        // precisão de milissegundo — computar o fingerprint a partir do valor de tick completo (ex.:
        // IClock.UtcNow) e comparar depois contra o valor truncado relido do banco produziria um falso-
        // positivo de adulteração em toda leitura, mesmo sem qualquer tampering real.
        var canonicalObservedAtUtc = TruncateToMilliseconds(observedAtUtc);
        var canonicalNow = TruncateToMilliseconds(now);

        // Validação/normalização PURA ANTES de abrir a transação (mesma técnica de SqlCanaryPlanStore) — a
        // única forma de obter Pass sem evidência real já é recusada aqui.
        var candidate = CanaryScenarioResult.Create(scenarioId, status, evidence, reasonCode, canonicalObservedAtUtc);
        var normalizedReasonCode = candidate.ReasonCode;
        var contentFingerprint = CanaryScenarioResult.ComputeContentFingerprint(scenarioId, status, evidence, normalizedReasonCode, canonicalObservedAtUtc);

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Gate de drift (escopo obrigatório item 5): planVersion precisa ainda ser a versão VIGENTE do
            // plano deste escopo, lida SOB A MESMA TRANSAÇÃO (HOLDLOCK) — uma nova versão do plano
            // autorizada concorrentemente invalida esta submissão fail-closed, ANTES de qualquer INSERT.
            int? latestPlanVersion;
            await using (var planCommand = new SqlCommand(LatestPlanVersionSql, connection.Connection, transaction))
            {
                planCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                planCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                var scalar = await planCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                latestPlanVersion = scalar is null or DBNull ? null : (int)scalar;
            }

            if (latestPlanVersion is null || latestPlanVersion.Value != planVersion)
            {
                var currentDescription = latestPlanVersion is { } value
                    ? value.ToString(CultureInfo.InvariantCulture)
                    : "nenhum plano autorizado";
                throw new CanaryPlanSupersededException(
                    $"A versão {planVersion.ToString(CultureInfo.InvariantCulture)} do plano de canário deste escopo " +
                    $"já não é a vigente (vigente: {currentDescription}) — submissão recusada (fail-closed).");
            }

            CanaryScenarioResult? current = null;
            RowSnapshot? currentRow = null;
            await using (var command = new SqlCommand(LockedResultsSql, connection.Connection, transaction))
            {
                BindResultScope(command, scope, planVersion, scenarioId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    currentRow = ReadRow(reader);
                    current = RehydrateResult(currentRow);
                }
            }

            if (currentRow is { } row && string.Equals(row.ContentFingerprint.Value, contentFingerprint.Value, StringComparison.Ordinal))
            {
                // Replay idêntico: converge sem inserir uma nova versão, mesmo sob concorrência.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current!;
            }

            var nextResultVersion = (currentRow?.ResultVersion ?? 0) + 1;
            var recordHash = CanaryScenarioResult.ComputeRecordHash(
                scope.Tenant.Value, scope.Project.Value, planVersion, scenarioId, nextResultVersion, status, evidence,
                normalizedReasonCode, canonicalObservedAtUtc, submittedBy, submittedByRole, correlation, canonicalNow, CurrentSchemaVersion, contentFingerprint);

            await using (var command = new SqlCommand(InsertSql, connection.Connection, transaction))
            {
                BindResultScope(command, scope, planVersion, scenarioId);
                command.Parameters.Add(new SqlParameter("@resultVersion", SqlDbType.Int) { Value = nextResultVersion });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)status });
                command.Parameters.Add(new SqlParameter("@evidenceKind", SqlDbType.TinyInt) { Value = (byte)evidence.Kind });
                command.Parameters.Add(new SqlParameter("@evidenceFingerprint", SqlDbType.Char, 64) { Value = evidence.Fingerprint.Value });
                command.Parameters.Add(new SqlParameter("@evidenceLocator", SqlDbType.NVarChar, 300) { Value = evidence.Locator });
                command.Parameters.Add(new SqlParameter("@reasonCode", SqlDbType.NVarChar, 200) { Value = normalizedReasonCode });
                command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(canonicalObservedAtUtc) });
                command.Parameters.Add(new SqlParameter("@submittedBy", SqlDbType.NVarChar, 200) { Value = submittedBy });
                command.Parameters.Add(new SqlParameter("@submittedByRole", SqlDbType.NVarChar, 50) { Value = submittedByRole });
                command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
                command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(canonicalNow) });
                command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.NVarChar, 100) { Value = CurrentSchemaVersion });
                command.Parameters.Add(new SqlParameter("@contentFingerprint", SqlDbType.Char, 64) { Value = contentFingerprint.Value });
                command.Parameters.Add(new SqlParameter("@recordHash", SqlDbType.Char, 64) { Value = recordHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CanaryScenarioResult?> GetLatestAsync(
        TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(LatestResultSql, connection.Connection);
        BindResultScope(command, scope, planVersion, scenarioId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? RehydrateResult(ReadRow(reader)) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CanaryScenarioId, CanaryScenarioResult>> GetAllLatestForPlanAsync(
        TenantScope scope, int planVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<CanaryScenarioId, CanaryScenarioResult>();
        await using var command = new SqlCommand(LatestForAllSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@planVersion", SqlDbType.Int) { Value = planVersion });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ReadRow(reader);
            var result = RehydrateResult(row);
            results[result.ScenarioId] = result;
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CanaryScenarioResult>> GetHistoryAsync(
        TenantScope scope, int planVersion, CanaryScenarioId scenarioId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var history = new List<CanaryScenarioResult>();
        await using var command = new SqlCommand(HistorySql, connection.Connection);
        BindResultScope(command, scope, planVersion, scenarioId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(RehydrateResult(ReadRow(reader)));
        }

        return history;
    }

    private static void BindResultScope(SqlCommand command, TenantScope scope, int planVersion, CanaryScenarioId scenarioId)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@planVersion", SqlDbType.Int) { Value = planVersion });
        command.Parameters.Add(new SqlParameter("@scenarioId", SqlDbType.NVarChar, 80) { Value = scenarioId.Value });
    }

    private static RowSnapshot ReadRow(SqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            new CanaryScenarioId(reader.GetString(3).TrimEnd()),
            reader.GetInt32(4),
            (CanaryScenarioStatus)reader.GetByte(5),
            (CanaryEvidenceKind)reader.GetByte(6),
            new Sha256Hash(reader.GetString(7).TrimEnd()),
            reader.GetString(8).TrimEnd(),
            reader.GetString(9).TrimEnd(),
            SqlJobMapping.ReadUtc(reader.GetDateTime(10)),
            reader.GetString(11).TrimEnd(),
            reader.GetString(12).TrimEnd(),
            new CorrelationId(reader.GetGuid(13)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(14)),
            reader.GetString(15).TrimEnd(),
            new Sha256Hash(reader.GetString(16).TrimEnd()),
            new Sha256Hash(reader.GetString(17).TrimEnd()));

    /// <summary>Revalida tamper-evidence (content_fingerprint + record_hash) contra os campos REALMENTE persistidos e projeta o resultado enxuto (sem os campos de auditoria, uso exclusivo desta camada).</summary>
    /// <exception cref="CanaryIntegrityViolationException">O content_fingerprint ou o record_hash persistido diverge do recomputado.</exception>
    private static CanaryScenarioResult RehydrateResult(RowSnapshot row)
    {
        var evidence = CanaryEvidenceReference.Rehydrate(row.EvidenceKind, row.EvidenceFingerprint, row.EvidenceLocator);

        var recomputedContentFingerprint = CanaryScenarioResult.ComputeContentFingerprint(row.ScenarioId, row.Status, evidence, row.ReasonCode, row.ObservedAtUtc);
        if (!string.Equals(recomputedContentFingerprint.Value, row.ContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new CanaryIntegrityViolationException(
                $"O content_fingerprint persistido para o resultado versão {row.ResultVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"do cenário '{row.ScenarioId.Value}' não corresponde ao fingerprint recomputado a partir dos campos " +
                "carregados — possivelmente adulterado ou corrompido.");
        }

        var recomputedRecordHash = CanaryScenarioResult.ComputeRecordHash(
            row.TenantId, row.ProjectId, row.PlanVersion, row.ScenarioId, row.ResultVersion, row.Status, evidence, row.ReasonCode,
            row.ObservedAtUtc, row.SubmittedBy, row.SubmittedByRole, row.Correlation, row.RecordedAtUtc, row.SchemaVersion, row.ContentFingerprint);
        if (!string.Equals(recomputedRecordHash.Value, row.RecordHash.Value, StringComparison.Ordinal))
        {
            throw new CanaryIntegrityViolationException(
                $"O record_hash persistido para o resultado versão {row.ResultVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"do cenário '{row.ScenarioId.Value}' não corresponde ao hash recomputado — possivelmente adulterado ou corrompido.");
        }

        return CanaryScenarioResult.Create(row.ScenarioId, row.Status, evidence, row.ReasonCode, row.ObservedAtUtc);
    }

    /// <summary>Trunca para milissegundos (mesma técnica de <see cref="Domain.Canary.CanaryPlan"/>) — a coluna DATETIME2(3) só armazena precisão de milissegundo; fingerprint/hash precisam ser computados sobre o MESMO valor que será relido, nunca sobre o tick completo pré-truncamento.</summary>
    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var truncatedTicks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);
        return new DateTimeOffset(truncatedTicks, TimeSpan.Zero);
    }

    private sealed record RowSnapshot(
        Guid TenantId,
        Guid ProjectId,
        int PlanVersion,
        CanaryScenarioId ScenarioId,
        int ResultVersion,
        CanaryScenarioStatus Status,
        CanaryEvidenceKind EvidenceKind,
        Sha256Hash EvidenceFingerprint,
        string EvidenceLocator,
        string ReasonCode,
        DateTimeOffset ObservedAtUtc,
        string SubmittedBy,
        string SubmittedByRole,
        CorrelationId Correlation,
        DateTimeOffset RecordedAtUtc,
        string SchemaVersion,
        Sha256Hash ContentFingerprint,
        Sha256Hash RecordHash);
}
