using System.Data;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Performance;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Performance;
using ArchiveBridge.Domain.Projects;
using ArchiveBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ArchiveBridge.Infrastructure.Performance;

/// <summary>
/// Custódia SQL append-only das execuções de benchmark (AB-I7-003 §1/§9). Tenant/projeto scoped (RLS +
/// filtro explícito por <c>project_id</c>). Duas tabelas (execução + medições por iteração), gravadas em
/// UMA transação: uma execução persistida nunca fica com medições parcialmente gravadas visíveis a outra
/// sessão.
/// </summary>
public sealed class SqlPerformanceBenchmarkResultStore(TenantConnectionFactory connectionFactory) : IPerformanceBenchmarkResultStore
{
    private const string InsertRunSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.performance_benchmark_runs
            (run_id, tenant_id, project_id, scenario_name, build_version, runtime_description, host_profile,
             dataset_name, dataset_size_bytes, dataset_item_count, dataset_seed, warmup_iterations, iterations,
             schema_version, recorded_at_utc)
        OUTPUT inserted.recorded_at_utc
        VALUES
            (@runId, @tenant, @project, @scenarioName, @buildVersion, @runtimeDescription, @hostProfile,
             @datasetName, @datasetSizeBytes, @datasetItemCount, @datasetSeed, @warmupIterations, @iterations,
             @schemaVersion, @recordedAt);
        """;

    private const string InsertMeasurementSql =
        """
        SET NOCOUNT ON;
        INSERT INTO dbo.performance_benchmark_measurements
            (run_id, iteration_index, tenant_id, project_id, wall_clock_ms, cpu_time_ms,
             peak_working_set_bytes, bytes_processed, items_processed, outcome)
        VALUES
            (@runId, @iterationIndex, @tenant, @project, @wallClockMs, @cpuTimeMs,
             @peakWorkingSetBytes, @bytesProcessed, @itemsProcessed, @outcome);
        """;

    private const string FindRunsSql =
        """
        SET NOCOUNT ON;
        SELECT TOP (@take) run_id, tenant_id, project_id, scenario_name, build_version, runtime_description,
               host_profile, dataset_name, dataset_size_bytes, dataset_item_count, dataset_seed,
               warmup_iterations, iterations, schema_version, recorded_at_utc
        FROM dbo.performance_benchmark_runs
        WHERE project_id = @project AND scenario_name = @scenarioName
        ORDER BY recorded_at_utc DESC;
        """;

    private const string FindMeasurementsSql =
        """
        SET NOCOUNT ON;
        SELECT iteration_index, wall_clock_ms, cpu_time_ms, peak_working_set_bytes, bytes_processed,
               items_processed, outcome
        FROM dbo.performance_benchmark_measurements
        WHERE run_id = @runId AND project_id = @project
        ORDER BY iteration_index ASC;
        """;

    private readonly TenantConnectionFactory _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public async Task<PerformanceBenchmarkRunRecord> SaveAsync(PerformanceBenchmarkRunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var scope = new TenantScope(run.Tenant, run.Project);
        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTime recordedAtDb;
            await using (var command = new SqlCommand(InsertRunSql, connection.Connection, transaction))
            {
                BindRun(command, run);
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("INSERT com OUTPUT não devolveu recorded_at_utc.");
                recordedAtDb = (DateTime)result;
            }

            foreach (var measurement in run.Measurements)
            {
                await using var command = new SqlCommand(InsertMeasurementSql, connection.Connection, transaction);
                BindMeasurement(command, run.Id, run.Tenant, run.Project, measurement);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // recorded_at_utc devolvido pelo INSERT (OUTPUT) é o valor REALMENTE persistido (truncado para a
            // precisão de milissegundo de DATETIME2(3)) — a Rehydrate garante que o objeto devolvido ao
            // chamador é byte-for-byte o que um FindRecentAsync subsequente leria de volta, nunca o valor em
            // memória potencialmente mais preciso calculado antes do INSERT.
            return PerformanceBenchmarkRunRecord.Rehydrate(
                run.Id, run.Tenant, run.Project, run.ScenarioName, run.BuildVersion, run.RuntimeDescription,
                run.HostProfile, run.Dataset, run.WarmupIterations, run.Iterations, run.Measurements,
                SqlJobMapping.ReadUtc(recordedAtDb));
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PerformanceBenchmarkRunRecord>> FindRecentAsync(
        TenantScope scope, string scenarioName, int take, CancellationToken cancellationToken)
    {
        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "take precisa ser pelo menos 1.");
        }

        await using var connection = await _connectionFactory.OpenForTenantAsync(scope, cancellationToken).ConfigureAwait(false);

        var runs = new List<(
            Guid RunId, TenantId Tenant, ProjectId Project, string ScenarioName, string BuildVersion,
            string RuntimeDescription, string HostProfile, string DatasetName, long DatasetSizeBytes,
            int DatasetItemCount, int DatasetSeed, int WarmupIterations, int Iterations, int SchemaVersion,
            DateTimeOffset RecordedAtUtc)>();

        await using (var command = new SqlCommand(FindRunsSql, connection.Connection))
        {
            command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
            command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = scope.Project.Value });
            command.Parameters.Add(new SqlParameter("@scenarioName", SqlDbType.NVarChar, 200) { Value = scenarioName });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runs.Add((
                    reader.GetGuid(0), new TenantId(reader.GetGuid(1)), new ProjectId(reader.GetGuid(2)),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetInt64(8), reader.GetInt32(9), reader.GetInt32(10),
                    reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13),
                    SqlJobMapping.ReadUtc(reader.GetDateTime(14))));
            }
        }

        var records = new List<PerformanceBenchmarkRunRecord>(runs.Count);
        foreach (var run in runs)
        {
            var measurements = await FindMeasurementsAsync(connection.Connection, run.RunId, scope.Project, cancellationToken)
                .ConfigureAwait(false);
            var dataset = new BenchmarkDatasetDescriptor(run.DatasetName, run.DatasetSizeBytes, run.DatasetItemCount, run.DatasetSeed);
            records.Add(PerformanceBenchmarkRunRecord.Rehydrate(
                new PerformanceBenchmarkRunId(run.RunId), run.Tenant, run.Project, run.ScenarioName, run.BuildVersion,
                run.RuntimeDescription, run.HostProfile, dataset, run.WarmupIterations, run.Iterations, measurements,
                run.RecordedAtUtc));
        }

        return records;
    }

    private static async Task<List<BenchmarkMeasurement>> FindMeasurementsAsync(
        SqlConnection connection, Guid runId, ProjectId project, CancellationToken cancellationToken)
    {
        var measurements = new List<BenchmarkMeasurement>();
        await using var command = new SqlCommand(FindMeasurementsSql, connection);
        command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            measurements.Add(new BenchmarkMeasurement(
                reader.GetInt32(0),
                reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                (BenchmarkIterationOutcome)reader.GetByte(6)));
        }

        return measurements;
    }

    private static void BindRun(SqlCommand command, PerformanceBenchmarkRunRecord run)
    {
        command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = run.Id.Value });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = run.Tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = run.Project.Value });
        command.Parameters.Add(new SqlParameter("@scenarioName", SqlDbType.NVarChar, 200) { Value = run.ScenarioName });
        command.Parameters.Add(new SqlParameter("@buildVersion", SqlDbType.NVarChar, 200) { Value = run.BuildVersion });
        command.Parameters.Add(new SqlParameter("@runtimeDescription", SqlDbType.NVarChar, 200) { Value = run.RuntimeDescription });
        command.Parameters.Add(new SqlParameter("@hostProfile", SqlDbType.NVarChar, 200) { Value = run.HostProfile });
        command.Parameters.Add(new SqlParameter("@datasetName", SqlDbType.NVarChar, 200) { Value = run.Dataset.Name });
        command.Parameters.Add(new SqlParameter("@datasetSizeBytes", SqlDbType.BigInt) { Value = run.Dataset.SizeBytes });
        command.Parameters.Add(new SqlParameter("@datasetItemCount", SqlDbType.Int) { Value = run.Dataset.ItemCount });
        command.Parameters.Add(new SqlParameter("@datasetSeed", SqlDbType.Int) { Value = run.Dataset.Seed });
        command.Parameters.Add(new SqlParameter("@warmupIterations", SqlDbType.Int) { Value = run.WarmupIterations });
        command.Parameters.Add(new SqlParameter("@iterations", SqlDbType.Int) { Value = run.Iterations });
        command.Parameters.Add(new SqlParameter("@schemaVersion", SqlDbType.Int) { Value = PerformanceBenchmarkRunRecord.SchemaVersion });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTime2) { Value = SqlJobMapping.ToDbUtc(run.RecordedAtUtc) });
    }

    private static void BindMeasurement(
        SqlCommand command, PerformanceBenchmarkRunId runId, TenantId tenant, ProjectId project, BenchmarkMeasurement measurement)
    {
        command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId.Value });
        command.Parameters.Add(new SqlParameter("@iterationIndex", SqlDbType.Int) { Value = measurement.IterationIndex });
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant.Value });
        command.Parameters.Add(new SqlParameter("@project", SqlDbType.UniqueIdentifier) { Value = project.Value });
        command.Parameters.Add(new SqlParameter("@wallClockMs", SqlDbType.Float) { Value = measurement.WallClockMs });
        command.Parameters.Add(new SqlParameter("@cpuTimeMs", SqlDbType.Float) { Value = (object?)measurement.CpuTimeMs ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@peakWorkingSetBytes", SqlDbType.BigInt) { Value = (object?)measurement.PeakWorkingSetBytes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@bytesProcessed", SqlDbType.BigInt) { Value = (object?)measurement.BytesProcessed ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@itemsProcessed", SqlDbType.BigInt) { Value = (object?)measurement.ItemsProcessed ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.TinyInt) { Value = (byte)measurement.Outcome });
    }
}
