using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.PstProcessing;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Custódia SQL append-only dos planos de particionamento (<see cref="PartitionPlan"/>) e suas partes.
/// Tenant/projeto scoped (RLS + filtro explícito por <c>project_id</c>), identidade da aplicação (nunca a
/// de manutenção). O plano e suas partes são gravados na MESMA transação: nunca existe plano canônico sem
/// as partes que o tornam canônico.
/// <para>
/// A idempotência é reforçada pela coluna <c>is_canonical</c>, gravada com o mesmo valor que
/// <see cref="PartitionPlan.IsCanonical"/> calcula no Domain (travada por CHECK constraint na migration) e
/// pelo índice único filtrado <c>UX_pst_partition_plans_canonical</c> sobre ela — o backstop de corrida
/// quando duas execuções concorrentes gravam o canônico da mesma identidade determinística.
/// </para>
/// </summary>
public sealed class SqlPartitionPlanStore(TenantConnectionFactory connectionFactory) : IPartitionPlanStore
{
    // Nome do índice único filtrado que reforça a canonicidade — usado para restringir a tradução de
    // SqlException 2601/2627 exatamente a este backstop esperado. Violação de OUTRA constraint única
    // (ex.: PK por colisão de GUID) nunca pode ser mascarada como corrida idempotente.
    private const string CanonicalIndexName = "UX_pst_partition_plans_canonical";

    private const string PlanColumns =
        "plan_id, tenant_id, project_id, artifact_id, inspection_id, source_hash, source_size_bytes, " +
        "plan_hash, policy_fingerprint, target_part_bytes, hard_part_bytes, planner_name, planner_version, " +
        "engine_name, engine_version, reason, correlation_id, planned_at_utc";

    private const string FindCanonicalSql =
        $"""
        SET NOCOUNT ON;
        SELECT {PlanColumns}
        FROM dbo.pst_partition_plans
        WHERE is_canonical = 1 AND artifact_id = @artifact AND project_id = @project AND plan_hash = @planHash;
        """;

    private const string FindByIdSql =
        $"""
        SET NOCOUNT ON;
        SELECT {PlanColumns}
        FROM dbo.pst_partition_plans
        WHERE plan_id = @planId AND project_id = @project;
        """;

    private const string FindPartsSql =
        """
        SET NOCOUNT ON;
        SELECT part_id, part_sequence, part_key, planned_size_bytes, covers_entire_source
        FROM dbo.pst_partition_plan_parts
        WHERE plan_id = @plan AND project_id = @project
        ORDER BY part_sequence;
        """;

    // OUTPUT inserted.* devolve as colunas EXATAMENTE como persistidas (ex.: planned_at_utc truncado para a
    // precisão de milissegundo de DATETIME2(3)) — SaveAsync nunca retorna ao chamador um valor em memória
    // que divergiria do que um réplay subsequente (FindCanonicalAsync) leria de volta.
    private const string InsertPlanSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.pst_partition_plans
            (plan_id, tenant_id, project_id, artifact_id, inspection_id, source_hash, source_size_bytes,
             plan_hash, policy_fingerprint, target_part_bytes, hard_part_bytes, planner_name, planner_version,
             engine_name, engine_version, outcome, reason, part_count, is_canonical, correlation_id, planned_at_utc)
        OUTPUT inserted.plan_id, inserted.tenant_id, inserted.project_id, inserted.artifact_id,
               inserted.inspection_id, inserted.source_hash, inserted.source_size_bytes, inserted.plan_hash,
               inserted.policy_fingerprint, inserted.target_part_bytes, inserted.hard_part_bytes,
               inserted.planner_name, inserted.planner_version, inserted.engine_name, inserted.engine_version,
               inserted.reason, inserted.correlation_id, inserted.planned_at_utc
        VALUES
            (@planId, @tenant, @project, @artifact, @inspection, @sourceHash, @sourceSize,
             @planHash, @policyFingerprint, @targetPartBytes, @hardPartBytes, @plannerName, @plannerVersion,
             @engineName, @engineVersion, @outcome, @reason, @partCount, @isCanonical, @correlation, @plannedAt);
        """;

    private const string InsertPartSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.pst_partition_plan_parts
            (part_id, plan_id, tenant_id, project_id, part_sequence, part_key, planned_size_bytes, covers_entire_source)
        VALUES
            (@partId, @planId, @tenant, @project, @sequence, @partKey, @plannedSize, @coversEntireSource);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<PartitionPlan?> FindCanonicalAsync(
        TenantScope scope, ArtifactId artifact, Sha256Hash planHash, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        PlanRow row;
        await using (var command = new SqlCommand(FindCanonicalSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = artifact.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@planHash", SqlDbType.Char, 64) { Value = planHash.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            row = ReadPlanRow(reader);
        }

        var parts = await ReadPartsAsync(connection.Connection, transaction: null, row.Id, scope, cancellationToken)
            .ConfigureAwait(false);
        return row.ToPlan(parts);
    }

    /// <inheritdoc />
    public async Task<PartitionPlan?> FindByIdAsync(TenantScope scope, PartitionPlanId id, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        PlanRow row;
        await using (var command = new SqlCommand(FindByIdSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@planId", SqlDbType.UniqueIdentifier) { Value = id.Value });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            row = ReadPlanRow(reader);
        }

        var parts = await ReadPartsAsync(connection.Connection, transaction: null, row.Id, scope, cancellationToken)
            .ConfigureAwait(false);
        return row.ToPlan(parts);
    }

    /// <inheritdoc />
    public async Task<PartitionPlan> SaveAsync(PartitionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var scope = new TenantScope(plan.Tenant, plan.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        try
        {
            PlanRow row;
            await using (var command = new SqlCommand(InsertPlanSql, connection.Connection, transaction))
            {
                BindPlan(command, plan);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                row = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? ReadPlanRow(reader)
                    : throw new InvalidOperationException("INSERT com OUTPUT não devolveu a linha persistida.");
            }

            foreach (var part in plan.Parts)
            {
                await using var command = new SqlCommand(InsertPartSql, connection.Connection, transaction);
                command.Parameters.Add(new SqlParameter("@partId", SqlDbType.UniqueIdentifier) { Value = part.Id.Value });
                command.Parameters.Add(new SqlParameter("@planId", SqlDbType.UniqueIdentifier) { Value = plan.Id.Value });
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = plan.Tenant.Value });
                command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = plan.Project.Value });
                command.Parameters.Add(new SqlParameter("@sequence", SqlDbType.Int) { Value = part.Sequence });
                command.Parameters.Add(new SqlParameter("@partKey", SqlDbType.Char, 64) { Value = part.PartKey.Value });
                command.Parameters.Add(new SqlParameter("@plannedSize", SqlDbType.BigInt) { Value = part.PlannedSizeBytes });
                command.Parameters.Add(new SqlParameter("@coversEntireSource", SqlDbType.Bit) { Value = part.CoversEntireSource });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // As partes são relidas do banco DENTRO da transação: o que volta ao chamador é exatamente o
            // que foi persistido (nunca o objeto em memória), igual ao padrão de OUTPUT inserted.* do plano.
            var persistedParts = await ReadPartsAsync(connection.Connection, transaction, row.Id, scope, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row.ToPlan(persistedParts);
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627 && IsCanonicalIndexViolation(sql))
        {
            await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw new PartitionPlanConflictException(
                "Um plano canônico concorrente já foi gravado para esta identidade determinística.", sql);
        }
        catch
        {
            // Nenhum plano parcial (linha do plano sem suas partes) sobrevive a uma falha no meio do
            // caminho: a transação é desfeita antes de propagar o erro.
            await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    // O descarte do SqlTransaction já desfaz o que não foi commitado; o rollback explícito acima existe
    // para deixar o fail-closed evidente. Se a transação já tiver terminado (ex.: falha DEPOIS do commit),
    // o rollback lança InvalidOperationException — que nunca pode mascarar a exceção original.
    private static async Task SafeRollbackAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Transação já concluída/descartada: nada a desfazer.
        }
    }

    // SqlException não expõe o nome do índice como propriedade estruturada — apenas na mensagem em texto
    // livre (comportamento padrão do driver). A tradução para PartitionPlanConflictException fica restrita
    // ao backstop de canonicidade esperado; qualquer OUTRA violação propaga como o erro inesperado que é.
    private static bool IsCanonicalIndexViolation(SqlException sql) =>
        sql.Message.Contains(CanonicalIndexName, StringComparison.Ordinal);

    private static async Task<List<PartitionPlanPart>> ReadPartsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        PartitionPlanId plan,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(FindPartsSql, connection, transaction);
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = plan.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var parts = new List<PartitionPlanPart>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            parts.Add(new PartitionPlanPart(
                new PartitionPlanPartId(reader.GetGuid(0)),
                reader.GetInt32(1),
                new Sha256Hash(reader.GetString(2).TrimEnd()),
                reader.GetInt64(3),
                reader.GetBoolean(4)));
        }

        return parts;
    }

    private static void BindPlan(SqlCommand command, PartitionPlan plan)
    {
        command.Parameters.Add(new SqlParameter("@planId", SqlDbType.UniqueIdentifier) { Value = plan.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = plan.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = plan.Project.Value });
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = plan.Source.Artifact.Value });
        command.Parameters.Add(new SqlParameter("@inspection", SqlDbType.UniqueIdentifier)
        { Value = plan.Source.Inspection is { } inspection ? inspection.Value : DBNull.Value });
        command.Parameters.Add(new SqlParameter("@sourceHash", SqlDbType.Char, 64) { Value = plan.Source.SourceHash.Value });
        command.Parameters.Add(new SqlParameter("@sourceSize", SqlDbType.BigInt)
        { Value = (object?)plan.Source.SourceSizeBytes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@planHash", SqlDbType.Char, 64) { Value = plan.PlanHash.Value });
        command.Parameters.Add(new SqlParameter("@policyFingerprint", SqlDbType.Char, 64) { Value = plan.Policy.Fingerprint.Value });
        command.Parameters.Add(new SqlParameter("@targetPartBytes", SqlDbType.BigInt) { Value = plan.Policy.TargetPartBytes });
        command.Parameters.Add(new SqlParameter("@hardPartBytes", SqlDbType.BigInt) { Value = plan.Policy.HardPartBytes });
        command.Parameters.Add(new SqlParameter("@plannerName", SqlDbType.NVarChar, 100) { Value = plan.Planner.Name });
        command.Parameters.Add(new SqlParameter("@plannerVersion", SqlDbType.NVarChar, 50) { Value = plan.Planner.Version });
        command.Parameters.Add(new SqlParameter("@engineName", SqlDbType.NVarChar, 100)
        { Value = (object?)plan.Source.EngineName ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@engineVersion", SqlDbType.NVarChar, 50)
        { Value = (object?)plan.Source.EngineVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)plan.Outcome });
        command.Parameters.Add(new SqlParameter("@reason", SqlDbType.TinyInt) { Value = (byte)plan.Reason });
        command.Parameters.Add(new SqlParameter("@partCount", SqlDbType.Int) { Value = plan.Parts.Count });
        command.Parameters.Add(new SqlParameter("@isCanonical", SqlDbType.Bit) { Value = plan.IsCanonical });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = plan.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@plannedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(plan.PlannedAtUtc) });
    }

    private static PlanRow ReadPlanRow(SqlDataReader reader)
    {
        var policy = PartitionPolicy.Create(reader.GetInt64(9), reader.GetInt64(10));
        var storedFingerprint = new Sha256Hash(reader.GetString(8).TrimEnd());

        // Integridade da política persistida: o fingerprint gravado (que participa da identidade do plano)
        // TEM de bater com o recalculado a partir dos limites gravados. Divergência significa linha
        // corrompida/adulterada — fail-closed, nunca reaproveitar um plano cuja política não se sustenta.
        if (policy.Fingerprint != storedFingerprint)
        {
            throw new InvalidOperationException(
                "Fingerprint de política persistido diverge dos limites persistidos no mesmo plano.");
        }

        return new PlanRow(
            new PartitionPlanId(reader.GetGuid(0)),
            new TenantId(reader.GetGuid(1)),
            new ProjectId(reader.GetGuid(2)),
            new PartitionPlanSource(
                new ArtifactId(reader.GetGuid(3)),
                reader.IsDBNull(4) ? null : new InspectionId(reader.GetGuid(4)),
                new Sha256Hash(reader.GetString(5).TrimEnd()),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)),
            new Sha256Hash(reader.GetString(7).TrimEnd()),
            policy,
            new PartitionPlannerIdentity(reader.GetString(11), reader.GetString(12)),
            (PartitionPlanReason)reader.GetByte(15),
            new CorrelationId(reader.GetGuid(16)),
            SqlJobMapping.ReadUtc(reader.GetDateTime(17)));
    }

    /// <summary>Linha do plano lida do banco, antes de anexar as partes (evita tupla anônima gigante).</summary>
    private sealed record PlanRow(
        PartitionPlanId Id,
        TenantId Tenant,
        ProjectId Project,
        PartitionPlanSource Source,
        Sha256Hash PlanHash,
        PartitionPolicy Policy,
        PartitionPlannerIdentity Planner,
        PartitionPlanReason Reason,
        CorrelationId Correlation,
        DateTimeOffset PlannedAtUtc)
    {
        public PartitionPlan ToPlan(IReadOnlyList<PartitionPlanPart> parts) =>
            PartitionPlan.Rehydrate(
                Id, Tenant, Project, Source, Policy, Planner, PlanHash, Reason, parts, Correlation, PlannedAtUtc);
    }
}
