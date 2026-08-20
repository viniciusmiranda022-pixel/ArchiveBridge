using System.Data;
using System.Globalization;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Jobs;

/// <summary>
/// Fila durável de Jobs sobre SQL Server. O claim é atômico (READPAST + UPDLOCK: um único vencedor)
/// com ordenação por prioridade EFETIVA (aging, para evitar starvation). Complete/fail/retry são
/// cercados por (tenant + projeto + job + owner_worker + lease_epoch) e limpam o owner ao sair de
/// Processing; a idempotência é decidida pela trilha de auditoria (época + worker + estado alvo),
/// então um worker diferente com a mesma época NÃO recebe replay. A RLS por SESSION_CONTEXT garante
/// isolamento entre tenants; o filtro explícito por project_id garante isolamento entre projetos.
/// </summary>
public sealed class SqlJobStore(TenantConnectionFactory connectionFactory, IClock clock, TimeSpan agingInterval)
    : IJobStore
{
    private const string ClaimSql =
        """
        SET NOCOUNT ON;
        DECLARE @claimed TABLE (job_id UNIQUEIDENTIFIER, project_id UNIQUEIDENTIFIER, lease_epoch BIGINT,
                                lease_expires_at_utc DATETIME2(3), attempt_count INT, prior_state TINYINT);
        ;WITH candidate AS (
            SELECT TOP (1) job_id
            FROM dbo.jobs WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE project_id = @project
              AND workload = @workload
              AND state IN (0, 2)
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now)
            ORDER BY (CAST(priority AS BIGINT) + DATEDIFF_BIG(SECOND, created_at_utc, @now) / @agingSeconds) DESC,
                     created_at_utc ASC
        )
        UPDATE j
        SET state = 1,
            owner_worker = @worker,
            lease_epoch = j.lease_epoch + 1,
            lease_expires_at_utc = @leaseExpires,
            attempt_count = j.attempt_count + 1,
            next_attempt_at_utc = NULL,
            updated_at_utc = @now
        OUTPUT inserted.job_id, inserted.project_id, inserted.lease_epoch, inserted.lease_expires_at_utc,
               inserted.attempt_count, deleted.state
        INTO @claimed
        FROM dbo.jobs j INNER JOIN candidate c ON c.job_id = j.job_id;

        INSERT INTO dbo.job_attempts (job_id, tenant_id, project_id, attempt_number, owner_worker, lease_epoch, started_at_utc)
        SELECT c.job_id, @tenant, c.project_id, c.attempt_count, @worker, c.lease_epoch, @now FROM @claimed c;

        INSERT INTO dbo.job_state_transitions
            (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
        SELECT c.job_id, @tenant, c.project_id, c.prior_state, 1, 1, c.lease_epoch, @worker, @correlation, @now FROM @claimed c;

        SELECT job_id, lease_epoch, lease_expires_at_utc, attempt_count FROM @claimed;
        """;

    private const string CreateSql =
        """
        SET NOCOUNT ON;
        IF NOT EXISTS (SELECT 1 FROM dbo.projects WHERE project_id = @projectId)
            INSERT INTO dbo.projects (project_id, tenant_id, created_at_utc) VALUES (@projectId, @tenant, @now);

        INSERT INTO dbo.jobs
            (job_id, tenant_id, project_id, workload, state, priority, attempt_count, lease_epoch,
             next_attempt_at_utc, created_at_utc, updated_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, @workload, 0, @priority, 0, 0, @now, @now, @now);

        INSERT INTO dbo.job_state_transitions
            (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
        VALUES
            (@jobId, @tenant, @projectId, NULL, 0, 0, 0, NULL, @correlation, @now);
        """;

    private const string TransitionSql =
        """
        SET NOCOUNT ON;
        DECLARE @applied TABLE (prior_state TINYINT);
        UPDATE dbo.jobs
        SET state = @toState,
            last_error_code = @lastError,
            next_attempt_at_utc = @nextAttempt,
            owner_worker = NULL,
            lease_expires_at_utc = NULL,
            updated_at_utc = @now
        OUTPUT deleted.state INTO @applied
        WHERE job_id = @jobId AND project_id = @project AND owner_worker = @worker
              AND lease_epoch = @epoch AND state = 1;

        IF EXISTS (SELECT 1 FROM @applied)
        BEGIN
            INSERT INTO dbo.job_state_transitions
                (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
            SELECT @jobId, @tenant, @project, a.prior_state, @toState, @reason, @epoch, @worker, @correlation, @now
            FROM @applied a;
            SELECT 0 AS outcome;
        END
        ELSE
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.jobs WHERE job_id = @jobId AND project_id = @project)
                SELECT 3 AS outcome;
            ELSE IF EXISTS (SELECT 1 FROM dbo.job_state_transitions
                            WHERE job_id = @jobId AND lease_epoch = @epoch AND to_state = @toState AND worker_id = @worker)
                SELECT 1 AS outcome;
            ELSE
                SELECT 2 AS outcome;
        END
        """;

    // Retry manual autorizado (Passo 7): elegível SOMENTE em RetryScheduled (state = 2), SEM fencing por
    // worker (não há lease ativo neste estado). Adianta next_attempt_at_utc para @now (nunca atrasa).
    //
    // Idempotência (ajustada em AB-7-002): dbo.job_retry_requests é um LIVRO-RAZÃO append-only — uma linha
    // POR chave já consumida, para sempre (não apenas a chave mais recente do Job). A leitura dessa linha
    // pela CHAVE PRIMÁRIA (tenant_id, project_id, idempotency_key) usa WITH (UPDLOCK, HOLDLOCK): isso
    // serializa qualquer concorrência sobre a MESMA chave — a segunda solicitação concorrente bloqueia até
    // a primeira confirmar/reverter e então enxerga a linha já gravada (replay), nunca correndo uma
    // segunda transição. MESMA chave + MESMO job = replay idempotente (nenhum efeito novo). MESMA chave +
    // job DIFERENTE = conflito determinístico, sem tocar o job B. Chave NOVA + job elegível = aplica
    // exatamente uma transição RetryExpedited (auto-loop RetryScheduled → RetryScheduled) e grava o
    // resultado canônico (next_attempt_at_utc aplicado) no ledger.
    private const string RequestManualRetrySql =
        """
        SET NOCOUNT ON;
        DECLARE @ledgerJob UNIQUEIDENTIFIER;

        SELECT @ledgerJob = job_id
        FROM dbo.job_retry_requests WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_id = @tenant AND project_id = @project AND idempotency_key = @key;

        IF @ledgerJob IS NOT NULL
        BEGIN
            IF @ledgerJob = @jobId
                SELECT 1 AS outcome; -- IdempotentReplay
            ELSE
                SELECT 4 AS outcome; -- IdempotencyConflict
        END
        ELSE
        BEGIN
            DECLARE @state TINYINT, @nextAttempt DATETIME2(3), @leaseEpoch BIGINT, @owner NVARCHAR(200);
            SELECT @state = state, @nextAttempt = next_attempt_at_utc, @leaseEpoch = lease_epoch, @owner = owner_worker
            FROM dbo.jobs WITH (UPDLOCK, ROWLOCK)
            WHERE job_id = @jobId AND project_id = @project;

            IF @state IS NULL
                SELECT 3 AS outcome; -- NotFound (inexistente ou cross-tenant/cross-project)
            ELSE IF @state <> 2
                SELECT 2 AS outcome; -- NotEligible
            ELSE
            BEGIN
                DECLARE @canonical DATETIME2(3) =
                    CASE WHEN @nextAttempt IS NULL OR @nextAttempt > @now THEN @now ELSE @nextAttempt END;

                UPDATE dbo.jobs
                SET next_attempt_at_utc = @canonical, updated_at_utc = @now
                WHERE job_id = @jobId AND project_id = @project AND state = 2;

                INSERT INTO dbo.job_state_transitions
                    (job_id, tenant_id, project_id, from_state, to_state, reason_code, lease_epoch, worker_id, correlation_id, occurred_at_utc)
                VALUES
                    (@jobId, @tenant, @project, 2, 2, @reason, @leaseEpoch, @owner, @correlation, @now);

                INSERT INTO dbo.job_retry_requests
                    (tenant_id, project_id, idempotency_key, job_id, applied_next_attempt_at_utc, created_at_utc)
                VALUES
                    (@tenant, @project, @key, @jobId, @canonical, @now);

                SELECT 0 AS outcome; -- Applied
            END
        END
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;
    private readonly long _agingSeconds = ComputeAgingSeconds(agingInterval);

    // agingInterval deve ser estritamente positivo; a granularidade de aging é de 1 segundo
    // (Ceiling garante divisor >= 1 sem coerção silenciosa de valores não positivos).
    private static long ComputeAgingSeconds(TimeSpan agingInterval)
    {
        if (agingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(agingInterval), agingInterval, "agingInterval deve ser maior que zero.");
        }

        return (long)Math.Ceiling(agingInterval.TotalSeconds);
    }

    /// <inheritdoc />
    public async Task<JobId> CreateAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var jobId = JobId.New();
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var sqlCommand = new SqlCommand(CreateSql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = command.Scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@projectId", SqlDbType.UniqueIdentifier) { Value = command.Scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = (byte)command.Workload });
                sqlCommand.Parameters.Add(new SqlParameter("@priority", SqlDbType.Int) { Value = command.Priority.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = command.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
                await sqlCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return jobId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ClaimedJob?> TryClaimNextAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock.UtcNow;
        var leaseExpires = SqlJobMapping.ToDbUtc(now + request.LeaseDuration);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(request.Scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClaimedJob? claimed = null;
            await using (var sqlCommand = new SqlCommand(ClaimSql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = request.Scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@workload", SqlDbType.TinyInt) { Value = (byte)request.Workload });
                sqlCommand.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = request.Worker.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = request.Scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = request.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@leaseExpires", SqlDbType.DateTime2) { Value = leaseExpires });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(now) });
                sqlCommand.Parameters.Add(new SqlParameter("@agingSeconds", SqlDbType.BigInt) { Value = _agingSeconds });

                await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    claimed = new ClaimedJob(
                        new JobId(reader.GetGuid(0)),
                        new LeaseEpoch(reader.GetInt64(1)),
                        SqlJobMapping.ReadUtc(reader.GetDateTime(2)),
                        reader.GetInt32(3));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<JobSnapshot?> GetAsync(TenantScope scope, JobId jobId, CancellationToken cancellationToken)
    {
        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var sqlCommand = new SqlCommand(
            $"SELECT {SqlJobMapping.JobColumns} FROM dbo.jobs WHERE job_id = @jobId AND project_id = @project;",
            tenantConnection.Connection);
        sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
        sqlCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });

        await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return SqlJobMapping.ReadSnapshot(reader);
        }

        return null;
    }

    /// <inheritdoc />
    public Task<JobCommandOutcome> CompleteAsync(LeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyTransitionAsync(command, toState: 3, reason: 2, lastError: null, nextAttempt: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobCommandOutcome> FailAsync(LeaseCommand command, ErrorCode errorCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyTransitionAsync(command, toState: 4, reason: 4, lastError: (byte)errorCode, nextAttempt: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<JobCommandOutcome> ScheduleRetryAsync(
        LeaseCommand command,
        ErrorCode errorCode,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyTransitionAsync(
            command,
            toState: 2,
            reason: 3,
            lastError: (byte)errorCode,
            nextAttempt: SqlJobMapping.ToDbUtc(nextAttemptAtUtc),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobRetryRequestOutcome> RequestManualRetryAsync(
        TenantScope scope,
        JobId jobId,
        Guid idempotencyKey,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        // Fail-closed na borda de infraestrutura (defesa em profundidade — o caso de uso também valida):
        // uma chave vazia é rejeitada ANTES de abrir conexão/transação — nenhuma escrita ocorre.
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(idempotencyKey));
        }

        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int outcome;
            await using (var sqlCommand = new SqlCommand(RequestManualRetrySql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = jobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = idempotencyKey });
                sqlCommand.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = (byte)ReasonCode.RetryExpedited });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });

                object? scalar;
                try
                {
                    scalar = await sqlCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException sql) when (sql.Number is 2601 or 2627)
                {
                    // Defesa em profundidade: a leitura do ledger com UPDLOCK+HOLDLOCK já serializa toda
                    // concorrência sobre a MESMA (tenant, projeto, chave) — este caminho não deveria ser
                    // alcançável — mas, se a PK de dbo.job_retry_requests ainda assim disparar (ex.: mudança
                    // futura de isolamento), o resultado seguro e determinístico é conflito, sem efeito.
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return JobRetryRequestOutcome.IdempotencyConflict;
                }

                outcome = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (JobRetryRequestOutcome)outcome;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JobCommandOutcome> ApplyTransitionAsync(
        LeaseCommand command,
        byte toState,
        byte reason,
        byte? lastError,
        DateTime? nextAttempt,
        CancellationToken cancellationToken)
    {
        var now = SqlJobMapping.ToDbUtc(_clock.UtcNow);

        await using var tenantConnection = await _connectionFactory
            .OpenForTenantAsync(command.Scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await tenantConnection.Connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int outcome;
            await using (var sqlCommand = new SqlCommand(TransitionSql, tenantConnection.Connection, transaction))
            {
                sqlCommand.Parameters.Add(new SqlParameter("@toState", SqlDbType.TinyInt) { Value = toState });
                sqlCommand.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = reason });
                sqlCommand.Parameters.Add(new SqlParameter("@lastError", SqlDbType.TinyInt) { Value = (object?)lastError ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@nextAttempt", SqlDbType.DateTime2) { Value = (object?)nextAttempt ?? DBNull.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@jobId", SqlDbType.UniqueIdentifier) { Value = command.JobId.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = command.Scope.Project.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = command.Worker.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@epoch", SqlDbType.BigInt) { Value = command.Epoch.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = command.Scope.Tenant.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = command.Correlation.Value });
                sqlCommand.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });

                var scalar = await sqlCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                outcome = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (JobCommandOutcome)outcome;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
