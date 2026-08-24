using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.WavePartitionBindings;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Domain.PstProcessing;
using ArchiveBridge.Domain.Waves;
using ArchiveBridge.Domain.WavePartitionBindings;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.WavePartitionBindings;

/// <summary>
/// Custódia SQL append-only dos vínculos wave → output de particionamento (AB-I5-010). Tenant/projeto
/// scoped (RLS + filtro explícito por <c>project_id</c>). A idempotência/concorrência é reforçada pelo
/// índice único <c>UX_wpob_canonical</c> sobre (tenant, projeto, wave, plano, parte).
/// </summary>
public sealed class SqlWavePartitionOutputBindingStore(TenantConnectionFactory connectionFactory) : IWavePartitionOutputBindingStore
{
    // Nome do índice único que reforça a canonicidade — restringe a tradução de SqlException 2601/2627
    // exatamente a este backstop esperado; violação de OUTRA constraint única nunca é mascarada como
    // corrida idempotente.
    private const string CanonicalIndexName = "UX_wpob_canonical";

    private const string Columns =
        "binding_id, tenant_id, project_id, wave_id, plan_id, part_id, execution_id, artifact_id, part_key, " +
        "output_hash, output_size_bytes, correlation_id, created_at_utc, binding_hash";

    private const string FindCanonicalSql =
        $"""
        SET NOCOUNT ON;
        SELECT {Columns}
        FROM dbo.wave_partition_output_bindings
        WHERE wave_id = @wave AND plan_id = @plan AND part_id = @part AND project_id = @project;
        """;

    private const string ListForWaveSql =
        $"""
        SET NOCOUNT ON;
        SELECT {Columns}
        FROM dbo.wave_partition_output_bindings
        WHERE wave_id = @wave AND project_id = @project
        ORDER BY created_at_utc ASC;
        """;

    // OUTPUT inserted.* devolve as colunas EXATAMENTE como persistidas (created_at_utc truncado para
    // DATETIME2(3)) — SaveAsync nunca retorna ao chamador um valor em memória que divergiria do que um
    // réplay subsequente (FindCanonicalAsync) leria de volta.
    private const string InsertSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.wave_partition_output_bindings
            (binding_id, tenant_id, project_id, wave_id, plan_id, part_id, execution_id, artifact_id, part_key,
             output_hash, output_size_bytes, correlation_id, created_at_utc, binding_hash)
        OUTPUT inserted.binding_id, inserted.tenant_id, inserted.project_id, inserted.wave_id, inserted.plan_id,
               inserted.part_id, inserted.execution_id, inserted.artifact_id, inserted.part_key,
               inserted.output_hash, inserted.output_size_bytes, inserted.correlation_id, inserted.created_at_utc,
               inserted.binding_hash
        VALUES
            (@bindingId, @tenant, @project, @wave, @plan, @part, @execution, @artifact, @partKey,
             @outputHash, @outputSize, @correlation, @createdAt, @bindingHash);
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<WavePartitionOutputBinding?> FindCanonicalAsync(
        TenantScope scope, WaveId wave, PartitionPlanId plan, PartitionPlanPartId part, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(FindCanonicalSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = plan.Value });
        command.Parameters.Add(new SqlParameter("@part", SqlDbType.UniqueIdentifier) { Value = part.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBinding(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WavePartitionOutputBinding>> ListForWaveAsync(
        TenantScope scope, WaveId wave, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(ListForWaveSql, connection.Connection);
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = wave.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<WavePartitionOutputBinding>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadBinding(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<WavePartitionOutputBinding> SaveAsync(WavePartitionOutputBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var scope = new TenantScope(binding.Tenant, binding.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = new SqlCommand(InsertSql, connection.Connection);
            BindBinding(command, binding);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadBinding(reader)
                : throw new InvalidOperationException("INSERT com OUTPUT não devolveu a linha persistida.");
        }
        catch (SqlException sql) when (sql.Number is 2601 or 2627 && IsCanonicalIndexViolation(sql))
        {
            throw new WavePartitionOutputBindingConflictException(
                "Um vínculo canônico concorrente já foi gravado para esta onda/plano/parte.", sql);
        }
    }

    // SqlException não expõe o nome do índice como propriedade estruturada — apenas na mensagem em texto
    // livre. A tradução para WavePartitionOutputBindingConflictException fica restrita ao backstop de
    // canonicidade esperado; qualquer OUTRA violação propaga como o erro inesperado que é.
    private static bool IsCanonicalIndexViolation(SqlException sql) =>
        sql.Message.Contains(CanonicalIndexName, StringComparison.Ordinal);

    private static void BindBinding(SqlCommand command, WavePartitionOutputBinding binding)
    {
        command.Parameters.Add(new SqlParameter("@bindingId", SqlDbType.UniqueIdentifier) { Value = binding.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = binding.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = binding.Project.Value });
        command.Parameters.Add(new SqlParameter("@wave", SqlDbType.UniqueIdentifier) { Value = binding.Wave.Value });
        command.Parameters.Add(new SqlParameter("@plan", SqlDbType.UniqueIdentifier) { Value = binding.Plan.Value });
        command.Parameters.Add(new SqlParameter("@part", SqlDbType.UniqueIdentifier) { Value = binding.Part.Value });
        command.Parameters.Add(new SqlParameter("@execution", SqlDbType.UniqueIdentifier) { Value = binding.Execution.Value });
        command.Parameters.Add(new SqlParameter("@artifact", SqlDbType.UniqueIdentifier) { Value = binding.Artifact.Value });
        command.Parameters.Add(new SqlParameter("@partKey", SqlDbType.Char, 64) { Value = binding.PartKey.Value });
        command.Parameters.Add(new SqlParameter("@outputHash", SqlDbType.Char, 64) { Value = binding.OutputHash.Value });
        command.Parameters.Add(new SqlParameter("@outputSize", SqlDbType.BigInt) { Value = binding.OutputSizeBytes });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = binding.Correlation.Value });
        command.Parameters.Add(new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(binding.CreatedAtUtc) });
        command.Parameters.Add(new SqlParameter("@bindingHash", SqlDbType.Char, 64) { Value = binding.BindingHash.Value });
    }

    private static WavePartitionOutputBinding ReadBinding(SqlDataReader reader) => WavePartitionOutputBinding.Rehydrate(
        new WavePartitionOutputBindingId(reader.GetGuid(0)),
        new TenantId(reader.GetGuid(1)),
        new ProjectId(reader.GetGuid(2)),
        new WaveId(reader.GetGuid(3)),
        new PartitionPlanId(reader.GetGuid(4)),
        new PartitionPlanPartId(reader.GetGuid(5)),
        new PartitionExecutionId(reader.GetGuid(6)),
        new ArtifactId(reader.GetGuid(7)),
        new Sha256Hash(reader.GetString(8).TrimEnd()),
        new Sha256Hash(reader.GetString(9).TrimEnd()),
        reader.GetInt64(10),
        new CorrelationId(reader.GetGuid(11)),
        SqlJobMapping.ReadUtc(reader.GetDateTime(12)),
        new Sha256Hash(reader.GetString(13).TrimEnd()));
}
