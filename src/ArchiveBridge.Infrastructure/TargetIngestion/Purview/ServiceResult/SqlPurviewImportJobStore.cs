using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.TargetIngestion.Purview.ServiceResult;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.TargetIngestion.Purview.ServiceResult;

/// <summary>
/// Persistência dos planos de import job do Purview e das observações do provider (AB-I6-001 itens 4-5,
/// 9-10). <see cref="CreatePlanAsync"/> locka TODOS os planos existentes da onda sob a MESMA transação
/// (mesmo padrão de <c>mapping_version</c> em <c>SqlPurviewMappingCsvStore.ReserveAsync</c>) e decide, sob
/// esse lock, tanto a próxima sequência de tentativa N+1 QUANTO se algum plano já existente converge pela
/// MESMA <c>evidence_fingerprint</c> — chamadas concorrentes com evidência canônica idêntica sempre
/// convergem para o MESMO plano, nunca alocam tentativas duplicadas para a mesma evidência (AB-I6-003
/// Blocker 3). <see cref="RecordObservationAsync"/> aplica, na MESMA transação curta, tanto a convergência
/// idempotente de replay quanto a recusa fail-closed de reassociação de provider ID — o vínculo
/// plano→provider é "amarrado" na tabela <c>purview_import_job_provider_bindings</c>, cujo índice único
/// <c>(tenant_id, project_id, provider_operation_id)</c> impede, no BANCO, que o MESMO provider ID seja
/// reivindicado por dois planos diferentes do escopo. RLS por SESSION_CONTEXT.
/// </summary>
public sealed class SqlPurviewImportJobStore(TenantConnectionFactory connectionFactory) : IPurviewImportJobStore
{
    private const int UniqueViolation = 2601;
    private const int PrimaryKeyViolation = 2627;

    private const string PlanColumns =
        "wave_id, attempt_sequence, tenant_id, project_id, planned_job_name, evidence_fingerprint, created_by, created_at_utc, plan_hash";

    private const string GetLatestPlanByFingerprintSql =
        $"""
        SELECT TOP (1) {PlanColumns} FROM dbo.purview_import_job_plans
        WHERE wave_id = @wave AND project_id = @project AND evidence_fingerprint = @fingerprint
        ORDER BY attempt_sequence DESC;
        """;

    private const string GetPlanByNameSql =
        $"SELECT {PlanColumns} FROM dbo.purview_import_job_plans WHERE wave_id = @wave AND project_id = @project AND planned_job_name = @name;";

    // AB-I6-003 Blocker 3: locka TODOS os planos existentes desta onda (mesmo predicado/força de lock da
    // antiga SELECT MAX) para servir DUAS decisões sob a MESMA seção crítica: (a) a próxima
    // attempt_sequence a alocar SE nenhum plano convergir, e (b) se algum plano JÁ existente tem a MESMA
    // evidence_fingerprint desta chamada — caso em que a chamada converge para ele em vez de alocar N+1.
    // Sem isso, duas chamadas concorrentes com evidência canônica IDÊNTICA que ambas leram
    // GetLatestPlanByFingerprintAsync como "nenhum plano ainda" fora da transação alocariam duas
    // tentativas para a MESMA evidência.
    private const string LockedPlansSql =
        $"""
        SELECT {PlanColumns} FROM dbo.purview_import_job_plans WITH (UPDLOCK, HOLDLOCK)
        WHERE wave_id = @wave AND project_id = @project
        ORDER BY attempt_sequence DESC;
        """;

    private const string InsertPlanSql =
        """
        INSERT INTO dbo.purview_import_job_plans
            (wave_id, attempt_sequence, tenant_id, project_id, planned_job_name, evidence_fingerprint, created_by, created_at_utc, plan_hash)
        VALUES
            (@wave, @attempt, @tenant, @project, @name, @fingerprint, @createdBy, @createdAt, @hash);
        """;

    private const string ObservationColumns =
        "wave_id, attempt_sequence, tenant_id, project_id, provider_operation_id, observed_status, observed_at_utc, " +
        "operator_label, recorded_at_utc, observation_hash";

    private const string GetLatestObservationSql =
        $"""
        SELECT TOP (1) {ObservationColumns} FROM dbo.purview_import_job_observations
        WHERE wave_id = @wave AND project_id = @project AND attempt_sequence = @attempt
        ORDER BY sequence_no DESC;
        """;

    private const string LockedBindingSql =
        "SELECT provider_operation_id FROM dbo.purview_import_job_provider_bindings WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE wave_id = @wave AND attempt_sequence = @attempt AND tenant_id = @tenant AND project_id = @project;";

    private const string InsertBindingSql =
        """
        INSERT INTO dbo.purview_import_job_provider_bindings (wave_id, attempt_sequence, tenant_id, project_id, provider_operation_id, bound_at_utc)
        VALUES (@wave, @attempt, @tenant, @project, @providerId, @boundAt);
        """;

    private const string FindIdenticalObservationSql =
        $"""
        SELECT TOP (1) {ObservationColumns} FROM dbo.purview_import_job_observations
        WHERE wave_id = @wave AND attempt_sequence = @attempt AND tenant_id = @tenant AND project_id = @project
          AND provider_operation_id = @providerId AND observed_status = @status AND observed_at_utc = @observedAt;
        """;

    private const string InsertObservationSql =
        """
        INSERT INTO dbo.purview_import_job_observations
            (observation_id, wave_id, attempt_sequence, tenant_id, project_id, provider_operation_id, observed_status,
             observed_at_utc, operator_label, recorded_at_utc, observation_hash)
        VALUES
            (@observationId, @wave, @attempt, @tenant, @project, @providerId, @status, @observedAt, @operatorLabel, @recordedAt, @hash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<PurviewImportJobPlan?> GetLatestPlanByFingerprintAsync(
        TenantScope scope, WaveId wave, PurviewMappingGenerationFingerprint fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetLatestPlanByFingerprintSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = fingerprint.Value.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPlan(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewImportJobPlan> CreatePlanAsync(
        TenantScope scope,
        WaveId wave,
        PurviewMappingGenerationFingerprint fingerprint,
        string createdBy,
        DateTimeOffset now,
        JobFence? fence,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql(), connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(now));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "PurviewImportJobPlan", cancellationToken).ConfigureAwait(false);
            }

            int nextAttempt = 1;
            PurviewImportJobPlan? converged = null;
            await using (var command = new SqlCommand(LockedPlansSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var first = true;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var existing = ReadPlan(reader);
                    if (first)
                    {
                        nextAttempt = existing.AttemptSequence + 1; // ORDER BY attempt_sequence DESC: primeira linha = maior tentativa.
                        first = false;
                    }

                    if (converged is null && existing.EvidenceFingerprint == fingerprint)
                    {
                        // AB-I6-003 Blocker 3: outra chamada (concorrente ou anterior) já planejou a MESMA
                        // evidência canônica sob este lock — converge para ela em vez de alocar N+1.
                        converged = existing;
                    }
                }
            }

            if (converged is not null)
            {
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return converged;
            }

            var plannedName = PurviewImportJobName.Compute(scope.Tenant, scope.Project, wave, nextAttempt);
            var plan = PurviewImportJobPlan.Create(scope.Tenant, scope.Project, wave, nextAttempt, plannedName, fingerprint, createdBy, now);

            await using (var command = new SqlCommand(InsertPlanSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = plan.Wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = plan.AttemptSequence });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = plan.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = plan.Project.Value });
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, 100) { Value = plan.PlannedJobName.Value });
                command.Parameters.Add(new SqlParameter("@fingerprint", SqlDbType.Char, 64) { Value = plan.EvidenceFingerprint.Value.Value });
                command.Parameters.Add(new SqlParameter("@createdBy", SqlDbType.NVarChar, 200) { Value = plan.CreatedBy });
                command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(plan.CreatedAtUtc) });
                command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = plan.PlanHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(now), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return plan;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PurviewImportJobPlan?> GetPlanByNameAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetPlanByNameSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.VarChar, 100) { Value = plannedJobName.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPlan(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewImportJobObservation?> GetLatestObservationAsync(
        TenantScope scope, WaveId wave, PurviewImportJobName plannedJobName, CancellationToken cancellationToken)
    {
        var plan = await GetPlanByNameAsync(scope, wave, plannedJobName, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetLatestObservationSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = plan.AttemptSequence });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObservation(reader, plannedJobName) : null;
    }

    /// <inheritdoc />
    public async Task<PurviewImportJobObservation> RecordObservationAsync(
        TenantScope scope, PurviewImportJobObservation observation, JobFence? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var plan = await GetPlanByNameAsync(scope, observation.Wave, observation.PlannedJobName, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewImportJobSourceNotFoundException(
                "Plano de import job inexistente/fora do escopo autorizado (fail-closed).");

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql(), connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(observation.RecordedAtUtc));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "PurviewImportJobObservation", cancellationToken).ConfigureAwait(false);
            }

            string? boundProviderId = null;
            await using (var command = new SqlCommand(LockedBindingSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = plan.Wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = plan.AttemptSequence });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                boundProviderId = scalar as string;
            }

            if (boundProviderId is not null)
            {
                if (!string.Equals(boundProviderId.TrimEnd(), observation.ProviderOperationId.Value, StringComparison.Ordinal))
                {
                    throw new PurviewImportJobIdentityConflictException(
                        "Este plano já está associado a um provider_operation_id diferente — reassociação recusada (fail-closed).");
                }
            }
            else
            {
                try
                {
                    await using var insertBinding = new SqlCommand(InsertBindingSql, connection.Connection, transaction);
                    insertBinding.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = plan.Wave.Value });
                    insertBinding.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = plan.AttemptSequence });
                    insertBinding.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                    insertBinding.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                    insertBinding.Parameters.Add(new SqlParameter("@providerId", SqlDbType.NVarChar, 300) { Value = observation.ProviderOperationId.Value });
                    insertBinding.Parameters.Add(new SqlParameter("@boundAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(observation.RecordedAtUtc) });
                    await insertBinding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException sql) when (sql.Number is UniqueViolation or PrimaryKeyViolation)
                {
                    throw new PurviewImportJobIdentityConflictException(
                        "Este provider_operation_id já está associado a outro plano/onda deste escopo — reassociação recusada (fail-closed).",
                        sql);
                }
            }

            await using (var command = new SqlCommand(FindIdenticalObservationSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = plan.Wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = plan.AttemptSequence });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@providerId", SqlDbType.NVarChar, 300) { Value = observation.ProviderOperationId.Value });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)observation.ObservedStatus });
                command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(observation.ObservedAtUtc) });

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Replay idempotente: observação lógica idêntica já registrada — nenhuma linha nova.
                    var existing = ReadObservation(reader, observation.PlannedJobName);
                    await reader.DisposeAsync().ConfigureAwait(false);
                    await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(observation.RecordedAtUtc), cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return existing;
                }
            }

            await using (var command = new SqlCommand(InsertObservationSql, connection.Connection, transaction))
            {
                command.Parameters.Add(new SqlParameter("@observationId", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
                command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = plan.Wave.Value });
                command.Parameters.Add(new SqlParameter("@attempt", SqlDbType.Int) { Value = plan.AttemptSequence });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                command.Parameters.Add(new SqlParameter("@providerId", SqlDbType.NVarChar, 300) { Value = observation.ProviderOperationId.Value });
                command.Parameters.Add(new SqlParameter("@status", SqlDbType.TinyInt) { Value = (byte)observation.ObservedStatus });
                command.Parameters.Add(new SqlParameter("@observedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(observation.ObservedAtUtc) });
                command.Parameters.Add(new SqlParameter("@operatorLabel", SqlDbType.NVarChar, 200) { Value = observation.OperatorLabel });
                command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(observation.RecordedAtUtc) });
                command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Char, 64) { Value = observation.ObservationHash.Value });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(observation.RecordedAtUtc), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return observation;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static string FenceGuardSql() => $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    // PlanColumns = wave_id(0), attempt_sequence(1), tenant_id(2), project_id(3), planned_job_name(4),
    // evidence_fingerprint(5), created_by(6), created_at_utc(7), plan_hash(8).
    private static PurviewImportJobPlan ReadPlan(SqlDataReader reader) =>
        PurviewImportJobPlan.Rehydrate(
            new TenantId(reader.GetGuid(2)),
            new ProjectId(reader.GetGuid(3)),
            new WaveId(reader.GetGuid(0)),
            reader.GetInt32(1),
            PurviewImportJobName.FromPersistedValue(reader.GetString(4).TrimEnd()),
            new PurviewMappingGenerationFingerprint(new Sha256Hash(reader.GetString(5).TrimEnd())),
            reader.GetString(6),
            SqlJobMapping.ReadUtc(reader.GetDateTime(7)),
            new Sha256Hash(reader.GetString(8).TrimEnd()));

    // ObservationColumns = wave_id(0), attempt_sequence(1), tenant_id(2), project_id(3),
    // provider_operation_id(4), observed_status(5), observed_at_utc(6), operator_label(7),
    // recorded_at_utc(8), observation_hash(9).
    private static PurviewImportJobObservation ReadObservation(SqlDataReader reader, PurviewImportJobName plannedJobName) =>
        PurviewImportJobObservation.Rehydrate(
            new TenantId(reader.GetGuid(2)),
            new ProjectId(reader.GetGuid(3)),
            new WaveId(reader.GetGuid(0)),
            plannedJobName,
            PurviewProviderOperationId.FromPersistedValue(reader.GetString(4).TrimEnd()),
            (PurviewImportJobObservedStatus)reader.GetByte(5),
            SqlJobMapping.ReadUtc(reader.GetDateTime(6)),
            reader.GetString(7),
            SqlJobMapping.ReadUtc(reader.GetDateTime(8)),
            new Sha256Hash(reader.GetString(9).TrimEnd()));
}
