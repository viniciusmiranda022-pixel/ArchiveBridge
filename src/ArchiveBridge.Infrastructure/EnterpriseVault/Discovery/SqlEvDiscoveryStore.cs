using System.Data;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.EnterpriseVault.Discovery;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.EnterpriseVault.Discovery;

/// <summary>
/// Persistência dos METADADOS de descoberta (nunca a evidência bruta). Protocolo recuperável em DUAS
/// transações curtas (Slice 2), sem I/O de filesystem sob transação. A reserva (<see cref="ReserveAsync"/>)
/// é ATÔMICA: valida o cercamento e, sob lock, verifica a pendente equivalente pelos TRÊS hashes
/// (configuração/evidência semântica/conteúdo) e a insere na MESMA transação — um índice único filtrado
/// (status Pending) impede duas versões Pending para a mesma evidência; uma colisão concorrente é
/// reconciliada relendo a pendente. A finalização promove Pending → terminal e substitui a anterior.
/// </summary>
public sealed class SqlEvDiscoveryStore(TenantConnectionFactory connectionFactory, IClock clock) : IEvDiscoveryStore
{
    private const int UniqueViolation = 2601;
    private const int PrimaryKeyViolation = 2627;

    private const string EnsureEnvironmentSql =
        """
        IF NOT EXISTS (SELECT 1 FROM dbo.ev_environments WHERE environment_id = @environment)
            INSERT INTO dbo.ev_environments (environment_id, tenant_id, project_id, site_name, directory_server)
            VALUES (@environment, @tenant, @project, @site, @directory);
        """;

    private const string MaxVersionSql =
        "SELECT ISNULL(MAX(discovery_version), 0) FROM dbo.ev_discovery_runs WHERE environment_id = @environment AND project_id = @project;";

    private const string LockedMaxVersionSql =
        "SELECT ISNULL(MAX(discovery_version), 0) FROM dbo.ev_discovery_runs WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE environment_id = @environment AND project_id = @project;";

    private const string SupersedeSql =
        "UPDATE dbo.ev_discovery_runs SET status = 6 WHERE environment_id = @environment AND project_id = @project AND status IN (2, 3, 4, 5);";

    private const string PromoteSql =
        "SET NOCOUNT OFF; UPDATE dbo.ev_discovery_runs SET status = result_status " +
        "WHERE environment_id = @environment AND project_id = @project AND discovery_version = @version AND status = 0;";

    private const string RecordColumns =
        "environment_id, discovery_version, run_id, status, result_status, result_code, selected_adapter, " +
        "adapter_version, observed_version, configuration_hash, evidence_hash, content_sha256, evidence_path";

    private const string GetCurrentSql =
        $"SELECT {RecordColumns} FROM dbo.ev_discovery_runs " +
        "WHERE environment_id = @environment AND project_id = @project AND status IN (2, 3, 4, 5);";

    private const string SelectByVersionSql =
        $"SELECT {RecordColumns} FROM dbo.ev_discovery_runs WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE environment_id = @environment AND project_id = @project AND discovery_version = @version;";

    // Inclui evidence_size_bytes (após as 13 colunas de RecordColumns, índice 13) para a reserva
    // reconciliada recuperar o TAMANHO REAL do SQL — nunca zero.
    private const string SelectPendingByHashesSql =
        $"SELECT TOP (1) {RecordColumns}, evidence_size_bytes FROM dbo.ev_discovery_runs WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE environment_id = @environment AND project_id = @project AND status = 0 " +
        "AND configuration_hash = @configHash AND evidence_hash = @semanticHash AND content_sha256 = @contentHash " +
        "ORDER BY discovery_version ASC;";

    private const string InsertRunSql =
        """
        INSERT INTO dbo.ev_discovery_runs
            (environment_id, discovery_version, tenant_id, project_id, run_id, status, result_status, result_code,
             selected_adapter, adapter_version, observed_version, discovery_schema_version, configuration_hash,
             evidence_hash, content_sha256, evidence_path, evidence_size_bytes, requested_by, worker_id, correlation_id,
             started_at_utc, completed_at_utc)
        VALUES
            (@environment, @version, @tenant, @project, @runId, 0, @resultStatus, @resultCode,
             @selectedAdapter, @adapterVersion, @observedVersion, @schema, @configHash,
             @semanticHash, @contentHash, @evidencePath, @evidenceSize, @requestedBy, @worker, @correlation,
             @startedAt, @completedAt);
        """;

    private const string InsertCapabilitySql =
        """
        INSERT INTO dbo.ev_capabilities
            (environment_id, discovery_version, tenant_id, project_id, capability_code, capability_version, availability, evidence_reference, blocking_reason)
        VALUES (@environment, @version, @tenant, @project, @code, @capVersion, @availability, @evidenceRef, @blockingReason);
        """;

    private const string InsertEvaluationSql =
        """
        INSERT INTO dbo.ev_adapter_evaluations
            (environment_id, discovery_version, tenant_id, project_id, adapter_id, adapter_version, compatibility, precedence,
             profile_id, runtime_observed, official_documentation, automated_fixture_validated, laboratory_validated)
        VALUES (@environment, @version, @tenant, @project, @adapterId, @adapterVersion, @compatibility, @precedence,
                @profileId, @runtimeObserved, @officialDoc, @automatedFixture, @labValidated);
        """;

    private const string InsertFindingSql =
        """
        INSERT INTO dbo.ev_discovery_findings
            (environment_id, discovery_version, tenant_id, project_id, result_code, capability_code, reason, discovered_at_utc, error_category)
        VALUES (@environment, @version, @tenant, @project, @resultCode, @capabilityCode, @reason, @discoveredAt, @errorCategory);
        """;

    private static readonly string FenceGuardSql = $"SET NOCOUNT ON;\n{SqlJobFence.GuardSql}";

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;
    private readonly IClock _clock = clock;

    /// <inheritdoc />
    public async Task<int> GetMaxVersionAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(MaxVersionSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int value ? value : 0;
    }

    /// <inheritdoc />
    public async Task<EvDiscoveryRecord?> GetUsableAsync(TenantScope scope, EvEnvironmentId environmentId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(GetCurrentSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<EvDiscoveryReservation> ReserveAsync(
        TenantScope scope, EvEnvironmentId environmentId, EvDiscoveryRunResult result,
        Sha256Hash configurationHash, Sha256Hash semanticEvidenceHash, Sha256Hash contentSha256, long evidenceSizeBytes,
        CorrelationId correlation, JobFence? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Invariantes de unicidade ANTES de abrir qualquer transação: uma duplicidade jamais chega ao INSERT
        // (nunca vira SqlException 2601/2627); a versão anterior permanece intacta (fail-closed).
        EvDiscoveryInvariants.Validate(result);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return await TryReserveAsync(
                    scope, environmentId, result, configurationHash, semanticEvidenceHash, contentSha256, evidenceSizeBytes, correlation, fence, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqlException exception) when (exception.Number is UniqueViolation or PrimaryKeyViolation)
            {
                // Colisão concorrente: se for a MESMA evidência, relê a pendente e a reaproveita; se for
                // outra evidência que colidiu no número de versão, recalcula N+1 e tenta de novo.
                var existing = await ReadPendingByHashesAsync(scope, environmentId, configurationHash, semanticEvidenceHash, contentSha256, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }
            }
        }

        throw new EvDiscoveryPersistenceException("Não foi possível reservar a descoberta após colisões concorrentes (fail-closed).");
    }

    private async Task<EvDiscoveryReservation> TryReserveAsync(
        TenantScope scope, EvEnvironmentId environmentId, EvDiscoveryRunResult result,
        Sha256Hash configurationHash, Sha256Hash semanticEvidenceHash, Sha256Hash contentSha256, long evidenceSizeBytes,
        CorrelationId correlation, JobFence? fence, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "EvDiscovery", cancellationToken).ConfigureAwait(false);
            }

            await using (var ensure = new SqlCommand(EnsureEnvironmentSql, connection.Connection, transaction))
            {
                ensure.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
                ensure.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
                ensure.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                ensure.Parameters.Add(new SqlParameter("@site", SqlDbType.NVarChar, 200) { Value = result.Environment.SiteName });
                ensure.Parameters.Add(new SqlParameter("@directory", SqlDbType.NVarChar, 260) { Value = result.Environment.DirectoryServer });
                await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Verificação ATÔMICA da pendente equivalente sob lock, na MESMA transação.
            var pending = await ReadPendingInTransactionAsync(connection.Connection, transaction, scope, environmentId, configurationHash, semanticEvidenceHash, contentSha256, cancellationToken)
                .ConfigureAwait(false);
            if (pending is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return pending;
            }

            int nextVersion;
            await using (var maxCommand = new SqlCommand(LockedMaxVersionSql, connection.Connection, transaction))
            {
                maxCommand.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
                maxCommand.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                var scalar = await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                nextVersion = (scalar is int value ? value : 0) + 1;
            }

            var logicalPath = new EvDiscoveryEvidenceDescriptor(scope, environmentId, nextVersion).LogicalPath;
            await InsertRunAsync(connection.Connection, transaction, scope, environmentId, nextVersion, result, configurationHash, semanticEvidenceHash, contentSha256, evidenceSizeBytes, logicalPath, correlation, fence, cancellationToken).ConfigureAwait(false);
            await InsertChildrenAsync(connection.Connection, transaction, scope, environmentId, nextVersion, result, cancellationToken).ConfigureAwait(false);

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new EvDiscoveryReservation(
                environmentId, nextVersion, result.RunId, logicalPath, configurationHash, semanticEvidenceHash, contentSha256, evidenceSizeBytes, Reconciled: false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<EvDiscoveryRecord> FinalizeAsync(
        TenantScope scope, EvDiscoveryReservation reservation, JobFence? fence,
        Func<CancellationToken, Task<EvDiscoveryEvidenceReference>> readPublishedEvidenceAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(readPublishedEvidenceAsync);

        // Lê o artefato publicado FORA da transação e CONFERE contra a reserva (caminho lógico + tamanho +
        // SHA-256 do conteúdo). Divergência ⇒ fail-closed, antes de abrir qualquer transação/lock.
        var published = await readPublishedEvidenceAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(published.LogicalPath, reservation.EvidenceLogicalPath, StringComparison.Ordinal)
            || published.SizeBytes != reservation.SizeBytes
            || !Matches(published.ContentSha256, reservation.ContentSha256))
        {
            throw new EvDiscoveryPersistenceException(
                "Evidência publicada diverge da reserva (caminho/tamanho/conteúdo) — finalização recusada (fail-closed).");
        }

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var guard = new SqlCommand(FenceGuardSql, connection.Connection, transaction))
            {
                SqlJobFence.Bind(guard, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow));
                await SqlJobFence.ExecuteGuardedAsync(guard, concurrencyError: -1, "EvDiscovery", cancellationToken).ConfigureAwait(false);
            }

            var current = await ReadByVersionAsync(connection.Connection, transaction, scope, reservation.Environment, reservation.DiscoveryVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new EvDiscoveryPersistenceException("Reserva de descoberta ausente na finalização (fail-closed).");

            if (!Matches(current.ConfigurationHash, reservation.ConfigurationHash)
                || !Matches(current.SemanticEvidenceHash, reservation.SemanticEvidenceHash)
                || !Matches(current.ContentSha256, reservation.ContentSha256))
            {
                throw new EvDiscoveryPersistenceException("Versão reservada não corresponde aos três hashes da finalização (fail-closed).");
            }

            if (current.Status != EvDiscoveryStatus.Pending)
            {
                await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            await using (var supersede = new SqlCommand(SupersedeSql, connection.Connection, transaction))
            {
                supersede.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = reservation.Environment.Value });
                supersede.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int promoted;
            await using (var promote = new SqlCommand(PromoteSql, connection.Connection, transaction))
            {
                promote.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = reservation.Environment.Value });
                promote.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
                promote.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = reservation.DiscoveryVersion });
                promoted = await promote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (promoted != 1)
            {
                throw new EvDiscoveryPersistenceException("Falha ao promover a reserva de descoberta (fail-closed).");
            }

            await SqlJobFence.RevalidateAsync(connection.Connection, transaction, fence, SqlJobMapping.ToDbUtc(_clock.UtcNow), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return await ReadByVersionAsync(connection.Connection, transaction: null, scope, reservation.Environment, reservation.DiscoveryVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new EvDiscoveryPersistenceException("Versão finalizada ilegível (fail-closed).");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static bool Matches(Sha256Hash a, Sha256Hash b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal);

    private async Task<EvDiscoveryReservation?> ReadPendingByHashesAsync(
        TenantScope scope, EvEnvironmentId environmentId, Sha256Hash configHash, Sha256Hash semanticHash, Sha256Hash contentHash, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        return await ReadPendingInTransactionAsync(connection.Connection, transaction: null, scope, environmentId, configHash, semanticHash, contentHash, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EvDiscoveryReservation?> ReadPendingInTransactionAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, EvEnvironmentId environmentId,
        Sha256Hash configHash, Sha256Hash semanticHash, Sha256Hash contentHash, CancellationToken cancellationToken)
    {
        var sql = transaction is null ? SelectPendingByHashesSql.Replace(" WITH (UPDLOCK, HOLDLOCK)", string.Empty, StringComparison.Ordinal) : SelectPendingByHashesSql;
        await using var command = transaction is null ? new SqlCommand(sql, connection) : new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = configHash.Value });
        command.Parameters.Add(new SqlParameter("@semanticHash", SqlDbType.Char, 64) { Value = semanticHash.Value });
        command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = contentHash.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var record = ReadRecord(reader);
        var sizeBytes = reader.GetInt64(13); // evidence_size_bytes — tamanho REAL persistido (nunca zero na reconciliação)
        return new EvDiscoveryReservation(
            record.Environment, record.DiscoveryVersion, record.RunId, record.EvidenceLogicalPath,
            record.ConfigurationHash, record.SemanticEvidenceHash, record.ContentSha256, sizeBytes, Reconciled: true);
    }

    private static async Task InsertRunAsync(
        SqlConnection connection, SqlTransaction transaction, TenantScope scope, EvEnvironmentId environmentId, int version,
        EvDiscoveryRunResult result, Sha256Hash configHash, Sha256Hash semanticHash, Sha256Hash contentHash, long evidenceSizeBytes,
        string logicalPath, CorrelationId correlation, JobFence? fence, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(InsertRunSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = result.RunId.Value });
        command.Parameters.Add(new SqlParameter("@resultStatus", SqlDbType.TinyInt) { Value = (byte)result.Status });
        command.Parameters.Add(new SqlParameter("@resultCode", SqlDbType.NVarChar, 64) { Value = result.ResultCode.Value });
        command.Parameters.Add(new SqlParameter("@selectedAdapter", SqlDbType.NVarChar, 200) { Value = (object?)result.CapabilitySet.AdapterId?.Value ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@adapterVersion", SqlDbType.Int) { Value = (object?)result.CapabilitySet.AdapterVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@observedVersion", SqlDbType.NVarChar, 100) { Value = result.Environment.ObservedVersion });
        command.Parameters.Add(new SqlParameter("@schema", SqlDbType.Int) { Value = result.CapabilitySet.DiscoverySchemaVersion });
        command.Parameters.Add(new SqlParameter("@configHash", SqlDbType.Char, 64) { Value = configHash.Value });
        command.Parameters.Add(new SqlParameter("@semanticHash", SqlDbType.Char, 64) { Value = semanticHash.Value });
        command.Parameters.Add(new SqlParameter("@contentHash", SqlDbType.Char, 64) { Value = contentHash.Value });
        command.Parameters.Add(new SqlParameter("@evidencePath", SqlDbType.NVarChar, 400) { Value = logicalPath });
        command.Parameters.Add(new SqlParameter("@evidenceSize", SqlDbType.BigInt) { Value = evidenceSizeBytes });
        command.Parameters.Add(new SqlParameter("@requestedBy", SqlDbType.NVarChar, 200) { Value = DBNull.Value });
        command.Parameters.Add(new SqlParameter("@worker", SqlDbType.NVarChar, 200) { Value = (object?)fence?.Worker.Value ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation.Value });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(result.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(result.CompletedAtUtc) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertChildrenAsync(
        SqlConnection connection, SqlTransaction transaction, TenantScope scope, EvEnvironmentId environmentId, int version,
        EvDiscoveryRunResult result, CancellationToken cancellationToken)
    {
        foreach (var capability in result.CapabilitySet.Capabilities)
        {
            await using var command = new SqlCommand(InsertCapabilitySql, connection, transaction);
            AddScope(command, scope, environmentId, version);
            command.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 64) { Value = capability.CapabilityCode.Value });
            command.Parameters.Add(new SqlParameter("@capVersion", SqlDbType.Int) { Value = capability.CapabilityVersion });
            command.Parameters.Add(new SqlParameter("@availability", SqlDbType.TinyInt) { Value = (byte)capability.Availability });
            command.Parameters.Add(new SqlParameter("@evidenceRef", SqlDbType.NVarChar, 200) { Value = capability.EvidenceReference });
            command.Parameters.Add(new SqlParameter("@blockingReason", SqlDbType.NVarChar, 400) { Value = (object?)capability.BlockingReason ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var evaluation in result.Selection.Evaluations)
        {
            await using var command = new SqlCommand(InsertEvaluationSql, connection, transaction);
            AddScope(command, scope, environmentId, version);
            command.Parameters.Add(new SqlParameter("@adapterId", SqlDbType.NVarChar, 200) { Value = evaluation.AdapterId.Value });
            command.Parameters.Add(new SqlParameter("@adapterVersion", SqlDbType.Int) { Value = evaluation.AdapterVersion });
            command.Parameters.Add(new SqlParameter("@compatibility", SqlDbType.TinyInt) { Value = (byte)evaluation.Compatibility });
            command.Parameters.Add(new SqlParameter("@precedence", SqlDbType.Int) { Value = evaluation.Precedence });
            command.Parameters.Add(new SqlParameter("@profileId", SqlDbType.NVarChar, 200) { Value = (object?)evaluation.ProfileId ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@runtimeObserved", SqlDbType.Bit) { Value = (object?)evaluation.Maturity?.RuntimeObserved ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@officialDoc", SqlDbType.Bit) { Value = (object?)evaluation.Maturity?.OfficialDocumentation ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@automatedFixture", SqlDbType.Bit) { Value = (object?)evaluation.Maturity?.AutomatedFixtureValidated ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@labValidated", SqlDbType.Bit) { Value = (object?)evaluation.Maturity?.LaboratoryValidated ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var finding in result.BlockingFindings)
        {
            await using var command = new SqlCommand(InsertFindingSql, connection, transaction);
            AddScope(command, scope, environmentId, version);
            command.Parameters.Add(new SqlParameter("@resultCode", SqlDbType.NVarChar, 64) { Value = finding.ResultCode.Value });
            command.Parameters.Add(new SqlParameter("@capabilityCode", SqlDbType.NVarChar, 64) { Value = (object?)finding.CapabilityCode?.Value ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@reason", SqlDbType.NVarChar, 400) { Value = finding.Reason });
            command.Parameters.Add(new SqlParameter("@discoveredAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(finding.DiscoveredAtUtc) });
            command.Parameters.Add(new SqlParameter("@errorCategory", SqlDbType.TinyInt) { Value = (byte)finding.ErrorCategory });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddScope(SqlCommand command, TenantScope scope, EvEnvironmentId environmentId, int version)
    {
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = scope.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
    }

    private static async Task<EvDiscoveryRecord?> ReadByVersionAsync(
        SqlConnection connection, SqlTransaction? transaction, TenantScope scope, EvEnvironmentId environmentId, int version, CancellationToken cancellationToken)
    {
        var sql = transaction is null ? SelectByVersionSql.Replace(" WITH (UPDLOCK, HOLDLOCK)", string.Empty, StringComparison.Ordinal) : SelectByVersionSql;
        await using var command = transaction is null ? new SqlCommand(sql, connection) : new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@environment", SqlDbType.UniqueIdentifier) { Value = environmentId.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    private static EvDiscoveryRecord ReadRecord(SqlDataReader reader) =>
        new(
            new EvEnvironmentId(reader.GetGuid(0)),
            reader.GetInt32(1),
            new DiscoveryRunId(reader.GetGuid(2)),
            (EvDiscoveryStatus)reader.GetByte(3),
            new EvDiscoveryResultCode(reader.GetString(5)),
            reader.IsDBNull(6) ? null : new EvAdapterId(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.GetString(8),
            new Sha256Hash(reader.GetString(9).TrimEnd()),
            new Sha256Hash(reader.GetString(10).TrimEnd()),
            new Sha256Hash(reader.GetString(11).TrimEnd()),
            reader.GetString(12));
}
