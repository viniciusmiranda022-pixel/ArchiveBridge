using System.Data;
using ArchiveBridge.Contracts.EnterpriseVault.Delta;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Connector;
using ArchiveBridge.Domain.EnterpriseVault.Delta;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Delta;

/// <summary>
/// Store SQL append-only da história de tentativas de execução de fase de delta (AB-4C-008 req 5/12/14).
/// <see cref="AppendAttemptAsync"/> computa <c>attempt_number</c> no servidor sob lock e, quando um
/// watermark é informado, persiste tentativa+watermark na MESMA transação (req 6: o watermark só se torna
/// canônico se a tentativa também for gravada). O índice único <c>UX_ev_delta_attempts_number</c> é o
/// backstop de concorrência — mesmo padrão de <c>SqlConnectorInventoryStore</c>.
/// </summary>
public sealed class SqlEvDeltaRunStore(TenantConnectionFactory connectionFactory) : IEvDeltaRunStore
{
    private const string Columns =
        "attempt_id, run_id, connector_id, external_archive_id, phase, canonical_idempotency_key, attempt_number, " +
        "strategy_name, strategy_version, previous_watermark_id, issued_watermark_id, outcome, blocking_reason, " +
        "started_at_utc, completed_at_utc";

    private const string SelectLatestByKeySql =
        $"""
        SELECT TOP (1) {Columns}
        FROM dbo.ev_delta_attempts
        WHERE tenant_id = @tenant AND project_id = @project AND canonical_idempotency_key = @key
        ORDER BY attempt_number DESC;
        """;

    private const string SelectAttemptsByRunSql =
        $"""
        SELECT {Columns}
        FROM dbo.ev_delta_attempts
        WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
        ORDER BY attempt_number ASC;
        """;

    // Resolve, sob lock, o run_id JÁ estabelecido para esta chave (quando existir) e o próximo
    // attempt_number — na MESMA leitura, para que run_id nunca divirja entre tentativas concorrentes
    // serializadas por este lock (a segunda tentativa sempre enxerga o run_id da primeira, já commitada).
    private const string SelectRunStateSql =
        """
        SELECT TOP (1) run_id, attempt_number
        FROM dbo.ev_delta_attempts WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND canonical_idempotency_key = @key
        ORDER BY attempt_number DESC;
        """;

    private const string InsertAttemptSql =
        $"""
        INSERT INTO dbo.ev_delta_attempts (tenant_id, project_id, {Columns})
        VALUES
            (@tenant, @project, @attemptId, @runId, @connector, @archiveId, @phase, @key, @attemptNumber, @strategyName,
             @strategyVersion, @previousWatermark, @issuedWatermark, @outcome, @blockingReason, @startedAt, @completedAt);
        """;

    private const string InsertWatermarkSql =
        """
        INSERT INTO dbo.ev_watermarks
            (watermark_id, tenant_id, project_id, connector_id, external_archive_id, phase, strategy_name,
             strategy_version, producing_execution_id, opaque_token, lineage_hash, issued_at_utc)
        VALUES
            (@wId, @tenant, @project, @wConnector, @wArchiveId, @wPhase, @wStrategyName, @wStrategyVersion,
             @wExecutionId, @wToken, @wLineageHash, @wIssuedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<EvDeltaAttemptRecord?> GetLatestByIdempotencyKeyAsync(
        TenantScope scope, Guid canonicalIdempotencyKey, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectLatestByKeySql, tenant.Connection);
        BindScopeAndKey(command, scope, canonicalIdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAttempt(reader) : null;
    }

    /// <inheritdoc />
    public async Task<EvDeltaAttemptRecord> AppendAttemptAsync(
        TenantScope scope, Guid canonicalIdempotencyKey, EvDeltaAttemptCandidate candidate, EvWatermark? watermarkToPersist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateCandidate(candidate, watermarkToPersist);

        var attemptId = EvDeltaAttemptId.New();

        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenant.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EvDeltaRunId runId;
            int attemptNumber;
            await using (var state = new SqlCommand(SelectRunStateSql, tenant.Connection, transaction))
            {
                BindScopeAndKey(state, scope, canonicalIdempotencyKey);
                await using var reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    runId = new EvDeltaRunId(reader.GetGuid(0));
                    attemptNumber = reader.GetInt32(1) + 1;
                }
                else
                {
                    runId = candidate.ExistingRun ?? EvDeltaRunId.New();
                    attemptNumber = 1;
                }
            }

            // O watermark é inserido ANTES da tentativa: FK_ev_delta_attempts_issued_watermark exige que a
            // linha referenciada já exista — ambas cometem juntas na MESMA transação, ou nenhuma comete.
            if (watermarkToPersist is not null)
            {
                await using var insertWatermark = new SqlCommand(InsertWatermarkSql, tenant.Connection, transaction);
                BindWatermark(insertWatermark, scope, watermarkToPersist);
                await insertWatermark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insertAttempt = new SqlCommand(InsertAttemptSql, tenant.Connection, transaction))
            {
                BindAttempt(insertAttempt, scope, canonicalIdempotencyKey, runId, attemptId, attemptNumber, candidate);
                await insertAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EvDeltaAttemptRecord(
                runId, attemptId, attemptNumber, candidate.Connector, candidate.ExternalArchiveId, candidate.Phase,
                candidate.Strategy, candidate.PreviousWatermark, candidate.IssuedWatermark, candidate.Outcome,
                candidate.BlockingReason, candidate.StartedAtUtc, candidate.CompletedAtUtc);
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627)
        {
            // Colisão de attempt_number sob a MESMA chave de idempotência: outra gravação concorrente venceu.
            // Nunca mascarada como sucesso — a Application releia via GetLatestByIdempotencyKeyAsync e converge.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new ConcurrencyException(
                "Colisão concorrente de attempt_number sob a mesma chave de idempotência de delta. Releia e tente novamente.", sql);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvDeltaAttemptRecord>> ListAttemptsAsync(TenantScope scope, EvDeltaRunId run, CancellationToken cancellationToken)
    {
        await using var tenant = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(SelectAttemptsByRunSql, tenant.Connection);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = run.Value });

        var attempts = new List<EvDeltaAttemptRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(ReadAttempt(reader));
        }

        return attempts;
    }

    private static void ValidateCandidate(EvDeltaAttemptCandidate candidate, EvWatermark? watermarkToPersist)
    {
        if (candidate.Outcome == EvDeltaRunOutcome.Completed)
        {
            if (watermarkToPersist is null || candidate.IssuedWatermark is null || watermarkToPersist.Id != candidate.IssuedWatermark.Value)
            {
                throw new ArgumentException(
                    "Uma tentativa Completed exige watermarkToPersist com Id igual a IssuedWatermark.", nameof(watermarkToPersist));
            }
        }
        else if (watermarkToPersist is not null)
        {
            throw new ArgumentException("Somente tentativas Completed podem persistir um watermark.", nameof(watermarkToPersist));
        }
    }

    private static void BindScopeAndKey(SqlCommand command, TenantScope scope, Guid canonicalIdempotencyKey)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = canonicalIdempotencyKey });
    }

    private static void BindAttempt(
        SqlCommand command, TenantScope scope, Guid canonicalIdempotencyKey, EvDeltaRunId runId, EvDeltaAttemptId attemptId,
        int attemptNumber, EvDeltaAttemptCandidate candidate)
    {
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@attemptId", SqlDbType.UniqueIdentifier) { Value = attemptId.Value });
        command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId.Value });
        command.Parameters.Add(new SqlParameter("@connector", SqlDbType.UniqueIdentifier) { Value = candidate.Connector.Value });
        command.Parameters.Add(new SqlParameter("@archiveId", SqlDbType.NVarChar, 300) { Value = candidate.ExternalArchiveId });
        command.Parameters.Add(new SqlParameter("@phase", SqlDbType.TinyInt) { Value = (byte)candidate.Phase });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = canonicalIdempotencyKey });
        command.Parameters.Add(new SqlParameter("@attemptNumber", SqlDbType.Int) { Value = attemptNumber });
        command.Parameters.Add(new SqlParameter("@strategyName", SqlDbType.NVarChar, 100)
        {
            Value = (object?)candidate.Strategy?.Name ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@strategyVersion", SqlDbType.Int)
        {
            Value = candidate.Strategy is { } strategy ? strategy.Version : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@previousWatermark", SqlDbType.UniqueIdentifier)
        {
            Value = candidate.PreviousWatermark is { } previous ? previous.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@issuedWatermark", SqlDbType.UniqueIdentifier)
        {
            Value = candidate.IssuedWatermark is { } issued ? issued.Value : DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)candidate.Outcome });
        command.Parameters.Add(new SqlParameter("@blockingReason", SqlDbType.NVarChar, 300)
        {
            Value = (object?)candidate.BlockingReason ?? DBNull.Value,
        });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(candidate.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(candidate.CompletedAtUtc) });
    }

    private static void BindWatermark(SqlCommand command, TenantScope scope, EvWatermark watermark)
    {
        command.Parameters.Add(new SqlParameter("@wId", SqlDbType.UniqueIdentifier) { Value = watermark.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@wConnector", SqlDbType.UniqueIdentifier) { Value = watermark.Connector.Value });
        command.Parameters.Add(new SqlParameter("@wArchiveId", SqlDbType.NVarChar, 300) { Value = watermark.ExternalArchiveId });
        command.Parameters.Add(new SqlParameter("@wPhase", SqlDbType.TinyInt) { Value = (byte)watermark.Phase });
        command.Parameters.Add(new SqlParameter("@wStrategyName", SqlDbType.NVarChar, 100) { Value = watermark.Strategy.Name });
        command.Parameters.Add(new SqlParameter("@wStrategyVersion", SqlDbType.Int) { Value = watermark.Strategy.Version });
        command.Parameters.Add(new SqlParameter("@wExecutionId", SqlDbType.UniqueIdentifier) { Value = watermark.ProducingExecutionId });
        command.Parameters.Add(new SqlParameter("@wToken", SqlDbType.NVarChar, 4000) { Value = watermark.OpaqueToken });
        command.Parameters.Add(new SqlParameter("@wLineageHash", SqlDbType.Char, 64) { Value = watermark.LineageHash.Value });
        command.Parameters.Add(new SqlParameter("@wIssuedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(watermark.IssuedAtUtc) });
    }

    private static EvDeltaAttemptRecord ReadAttempt(SqlDataReader reader) =>
        new(
            new EvDeltaRunId(reader.GetGuid(1)),
            new EvDeltaAttemptId(reader.GetGuid(0)),
            reader.GetInt32(6),
            new ConnectorId(reader.GetGuid(2)),
            reader.GetString(3),
            (EvDeltaPhase)reader.GetByte(4),
            reader.IsDBNull(7) ? null : new EvDeltaStrategyId(reader.GetString(7), reader.GetInt32(8)),
            reader.IsDBNull(9) ? null : new WatermarkId(reader.GetGuid(9)),
            reader.IsDBNull(10) ? null : new WatermarkId(reader.GetGuid(10)),
            (EvDeltaRunOutcome)reader.GetByte(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            SqlJobMapping.ReadUtc(reader.GetDateTime(13)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(14)));
}
