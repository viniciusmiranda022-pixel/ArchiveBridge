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
/// Custódia SQL append-only das execuções de partição (<see cref="PartitionExecutionRecord"/>) — o
/// manifesto durável do <c>PartCreated</c> (Slice 4B, Passo 3). Tenant/projeto scoped (RLS + filtro
/// explícito por <c>project_id</c>), identidade da aplicação. Diferente de <see cref="SqlPartitionPlanStore"/>,
/// esta store NUNCA grava uma tentativa fracassada: uma linha só é inserida depois que o writer confirmou o
/// output por reabertura/reinspeção — o INSERT em si é o checkpoint durável final da execução.
/// <para>
/// A idempotência/concorrência é reforçada pelo índice único <c>UX_pst_partition_executions_canonical</c>
/// sobre (tenant, projeto, plano, parte) — o backstop de corrida quando duas execuções concorrentes do
/// mesmo plano tentam persistir o resultado ao mesmo tempo.
/// </para>
/// </summary>
public sealed class SqlPartitionExecutionStore(TenantConnectionFactory connectionFactory) : IPartitionExecutionStore
{
    // Nome do índice único que reforça a canonicidade — usado para restringir a tradução de SqlException
    // 2601/2627 exatamente a este backstop esperado. Violação de OUTRA constraint única (ex.: PK por
    // colisão de GUID) nunca pode ser mascarada como corrida idempotente.
    private const string CanonicalIndexName = "UX_pst_partition_executions_canonical";

    private const string Columns =
        "execution_id, tenant_id, project_id, artifact_id, plan_id, part_id, plan_hash, part_sequence, " +
        "part_key, source_hash, source_size_bytes, output_hash, output_size_bytes, executor_name, " +
        "executor_version, correlation_id, started_at_utc, completed_at_utc";

    private const string FindCanonicalSql =
        $"""
        SET NOCOUNT ON;
        SELECT {Columns}
        FROM dbo.pst_partition_executions
        WHERE plan_id = @plan AND part_id = @part AND project_id = @project;
        """;

    // OUTPUT inserted.* devolve as colunas EXATAMENTE como persistidas (ex.: started_at_utc/completed_at_utc
    // truncados para a precisão de milissegundo de DATETIME2(3)) — SaveAsync nunca retorna ao chamador um
    // valor em memória que divergiria do que um réplay subsequente (FindCanonicalAsync) leria de volta.
    private const string InsertSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.pst_partition_executions
            (execution_id, tenant_id, project_id, artifact_id, plan_id, part_id, plan_hash, part_sequence,
             part_key, source_hash, source_size_bytes, output_hash, output_size_bytes, executor_name,
             executor_version, correlation_id, started_at_utc, completed_at_utc)
        OUTPUT inserted.execution_id, inserted.tenant_id, inserted.project_id, inserted.artifact_id,
               inserted.plan_id, inserted.part_id, inserted.plan_hash, inserted.part_sequence,
               inserted.part_key, inserted.source_hash, inserted.source_size_bytes, inserted.output_hash,
               inserted.output_size_bytes, inserted.executor_name, inserted.executor_version,
               inserted.correlation_id, inserted.started_at_utc, inserted.completed_at_utc
        VALUES
            (@executionId, @tenant, @project, @artifact, @plan, @part, @planHash, @partSequence,
             @partKey, @sourceHash, @sourceSize, @outputHash, @outputSize, @executorName,
             @executorVersion, @correlation, @startedAt, @completedAt);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<PartitionExecutionRecord?> FindCanonicalAsync(
        TenantScope scope, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(FindCanonicalSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = plan.Value });
        command.Parameters.Add(new SqlParameter("@part", SqlDbType.UniqueIdentifier) { Value = part.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<PartitionExecutionRecord> SaveAsync(PartitionExecutionRecord execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var scope = new TenantScope(execution.Tenant, execution.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = new SqlCommand(InsertSql, connection.Connection);
            BindExecution(command, execution);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadRecord(reader)
                : throw new InvalidOperationException("INSERT com OUTPUT não devolveu a linha persistida.");
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627 && IsCanonicalIndexViolation(sql))
        {
            throw new PartitionExecutionConflictException(
                "Uma execução canônica concorrente já foi gravada para este plano/parte.", sql);
        }
    }

    // SqlException não expõe o nome do índice como propriedade estruturada — apenas na mensagem em texto
    // livre (comportamento padrão do driver). A tradução para PartitionExecutionConflictException fica
    // restrita ao backstop de canonicidade esperado; qualquer OUTRA violação propaga como o erro inesperado que é.
    private static bool IsCanonicalIndexViolation(SqlException sql) =>
        sql.Message.Contains(CanonicalIndexName, StringComparison.Ordinal);

    private static void BindExecution(SqlCommand command, PartitionExecutionRecord execution)
    {
        command.Parameters.Add(new SqlParameter("@executionId", SqlDbType.UniqueIdentifier) { Value = execution.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = execution.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = execution.Project.Value });
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = execution.Artifact.Value });
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = execution.Plan.Value });
        command.Parameters.Add(new SqlParameter("@part", SqlDbType.UniqueIdentifier) { Value = execution.Part.Value });
        command.Parameters.Add(new SqlParameter("@planHash", SqlDbType.Char, 64) { Value = execution.PlanHash.Value });
        command.Parameters.Add(new SqlParameter("@partSequence", SqlDbType.Int) { Value = execution.PartSequence });
        command.Parameters.Add(new SqlParameter("@partKey", SqlDbType.Char, 64) { Value = execution.PartKey.Value });
        command.Parameters.Add(new SqlParameter("@sourceHash", SqlDbType.Char, 64) { Value = execution.SourceHash.Value });
        command.Parameters.Add(new SqlParameter("@sourceSize", SqlDbType.BigInt) { Value = execution.SourceSizeBytes });
        command.Parameters.Add(new SqlParameter("@outputHash", SqlDbType.Char, 64) { Value = execution.OutputHash.Value });
        command.Parameters.Add(new SqlParameter("@outputSize", SqlDbType.BigInt) { Value = execution.OutputSizeBytes });
        command.Parameters.Add(new SqlParameter("@executorName", SqlDbType.NVarChar, 100) { Value = execution.Executor.Name });
        command.Parameters.Add(new SqlParameter("@executorVersion", SqlDbType.NVarChar, 50) { Value = execution.Executor.Version });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = execution.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@startedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(execution.StartedAtUtc) });
        command.Parameters.Add(new SqlParameter("@completedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(execution.CompletedAtUtc) });
    }

    private static PartitionExecutionRecord ReadRecord(SqlDataReader reader) => PartitionExecutionRecord.Rehydrate(
        new PartitionExecutionId(reader.GetGuid(0)),
        new TenantId(reader.GetGuid(1)),
        new ProjectId(reader.GetGuid(2)),
        new ArtifactId(reader.GetGuid(3)),
        new PartitionPlanId(reader.GetGuid(4)),
        new PartitionPlanPartId(reader.GetGuid(5)),
        new Sha256Hash(reader.GetString(6).TrimEnd()),
        reader.GetInt32(7),
        new Sha256Hash(reader.GetString(8).TrimEnd()),
        new Sha256Hash(reader.GetString(9).TrimEnd()),
        reader.GetInt64(10),
        new Sha256Hash(reader.GetString(11).TrimEnd()),
        reader.GetInt64(12),
        new PartitionExecutorIdentity(reader.GetString(13), reader.GetString(14)),
        new CorrelationId(reader.GetGuid(15)),
        SqlJobMapping.ReadUtc(reader.GetDateTime(16)),
        SqlJobMapping.ReadUtc(reader.GetDateTime(17)));
}
