using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Performance;

/// <summary>Identidade de uma execução de benchmark, gerada pelo servidor.</summary>
public readonly record struct PerformanceBenchmarkRunId(Guid Value)
{
    /// <summary>Gera uma nova identidade de execução de benchmark.</summary>
    public static PerformanceBenchmarkRunId New() => new(Guid.NewGuid());
}

/// <summary>
/// Checkpoint IMUTÁVEL e append-only de UMA execução do <c>BenchmarkHarness</c> (AB-I7-003 §1/§5):
/// registra a versão do build, runtime, host profile, dataset (sanitizado), warmup/iterações e as
/// medições por iteração — a evidência reproduzível de performance/capacidade sobre a qual o baseline e a
/// comparação de regressão são construídos. Nunca uma tentativa parcial: só é criado depois que o harness
/// concluiu todas as iterações (com ou sem erro por iteração — erro de iteração é evidência, não motivo
/// para descartar a execução inteira).
/// </summary>
public sealed class PerformanceBenchmarkRunRecord
{
    /// <summary>Versão do schema deste tipo — participa da evidência para permitir migração futura sem ambiguidade.</summary>
    public const int SchemaVersion = 1;

    private readonly List<BenchmarkMeasurement> _measurements;

    private PerformanceBenchmarkRunRecord(
        PerformanceBenchmarkRunId id,
        TenantId tenant,
        ProjectId project,
        string scenarioName,
        string buildVersion,
        string runtimeDescription,
        string hostProfile,
        BenchmarkDatasetDescriptor dataset,
        int warmupIterations,
        int iterations,
        List<BenchmarkMeasurement> measurements,
        DateTimeOffset recordedAtUtc)
    {
        Id = id;
        Tenant = tenant;
        Project = project;
        ScenarioName = scenarioName;
        BuildVersion = buildVersion;
        RuntimeDescription = runtimeDescription;
        HostProfile = hostProfile;
        Dataset = dataset;
        WarmupIterations = warmupIterations;
        Iterations = iterations;
        _measurements = measurements;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Cria o registro de uma execução de benchmark CONCLUÍDA (todas as iterações já rodaram).</summary>
    /// <exception cref="ArgumentException">Invariante estrutural violado.</exception>
    public static PerformanceBenchmarkRunRecord Complete(
        PerformanceBenchmarkRunId id,
        TenantId tenant,
        ProjectId project,
        string scenarioName,
        string buildVersion,
        string runtimeDescription,
        string hostProfile,
        BenchmarkDatasetDescriptor dataset,
        int warmupIterations,
        int iterations,
        IReadOnlyList<BenchmarkMeasurement> measurements,
        DateTimeOffset recordedAtUtc) =>
        Build(
            id, tenant, project, scenarioName, buildVersion, runtimeDescription, hostProfile, dataset,
            warmupIterations, iterations, measurements, recordedAtUtc,
            wrap: message => new ArgumentException(message));

    /// <summary>
    /// Reconstrói uma execução JÁ PERSISTIDA (uso exclusivo da camada de persistência). Reaplica os MESMOS
    /// invariantes estruturais de <see cref="Complete"/> — uma linha corrompida/adulterada falha fechado em
    /// vez de ser devolvida em réplay.
    /// </summary>
    /// <exception cref="PerformanceBenchmarkRunIntegrityViolationException">A linha persistida viola um invariante estrutural.</exception>
    public static PerformanceBenchmarkRunRecord Rehydrate(
        PerformanceBenchmarkRunId id,
        TenantId tenant,
        ProjectId project,
        string scenarioName,
        string buildVersion,
        string runtimeDescription,
        string hostProfile,
        BenchmarkDatasetDescriptor dataset,
        int warmupIterations,
        int iterations,
        IReadOnlyList<BenchmarkMeasurement> measurements,
        DateTimeOffset recordedAtUtc) =>
        Build(
            id, tenant, project, scenarioName, buildVersion, runtimeDescription, hostProfile, dataset,
            warmupIterations, iterations, measurements, recordedAtUtc,
            wrap: message => new PerformanceBenchmarkRunIntegrityViolationException(
                $"Execução de benchmark persistida viola um invariante estrutural do Domain: {message}"));

    private static PerformanceBenchmarkRunRecord Build(
        PerformanceBenchmarkRunId id,
        TenantId tenant,
        ProjectId project,
        string scenarioName,
        string buildVersion,
        string runtimeDescription,
        string hostProfile,
        BenchmarkDatasetDescriptor dataset,
        int warmupIterations,
        int iterations,
        IReadOnlyList<BenchmarkMeasurement> measurements,
        DateTimeOffset recordedAtUtc,
        Func<string, Exception> wrap)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(measurements);

        if (tenant.Value == Guid.Empty)
        {
            throw wrap("Tenant é obrigatório.");
        }

        if (project.Value == Guid.Empty)
        {
            throw wrap("Projeto é obrigatório.");
        }

        var name = TextValue.Require(scenarioName, nameof(scenarioName), 200);
        var build = TextValue.Require(buildVersion, nameof(buildVersion), 200);
        var runtime = TextValue.Require(runtimeDescription, nameof(runtimeDescription), 200);
        var host = TextValue.Require(hostProfile, nameof(hostProfile), 200);

        if (warmupIterations < 0)
        {
            throw wrap("warmupIterations não pode ser negativo.");
        }

        if (iterations < 1)
        {
            throw wrap("iterations precisa ser pelo menos 1.");
        }

        // Determinismo/completude do resultado (AB-I7-003 §9): exatamente uma medição por iteração
        // executada, índices 0..iterations-1 sem lacuna nem duplicata — nunca uma iteração "perdida"
        // silenciosamente nem uma sobra de uma execução anterior reaproveitada por engano.
        if (measurements.Count != iterations)
        {
            throw wrap($"Esperava exatamente {iterations} medições (uma por iteração), recebeu {measurements.Count}.");
        }

        var seenIndices = new HashSet<int>();
        foreach (var measurement in measurements)
        {
            if (!seenIndices.Add(measurement.IterationIndex))
            {
                throw wrap($"Índice de iteração duplicado: {measurement.IterationIndex}.");
            }
        }

        for (var expected = 0; expected < iterations; expected++)
        {
            if (!seenIndices.Contains(expected))
            {
                throw wrap($"Índice de iteração ausente: {expected}.");
            }
        }

        var ordered = measurements.OrderBy(measurement => measurement.IterationIndex).ToList();

        return new PerformanceBenchmarkRunRecord(
            id, tenant, project, name, build, runtime, host, dataset, warmupIterations, iterations, ordered, recordedAtUtc);
    }

    /// <summary>Identidade da execução.</summary>
    public PerformanceBenchmarkRunId Id { get; }

    /// <summary>Tenant do escopo autorizado (nunca inferido do cliente).</summary>
    public TenantId Tenant { get; }

    /// <summary>Projeto do escopo autorizado (nunca inferido do cliente).</summary>
    public ProjectId Project { get; }

    /// <summary>Nome estável do cenário (ex.: <c>HashStreaming</c>, <c>PartitionExecution</c>).</summary>
    public string ScenarioName { get; }

    /// <summary>Versão do build sob teste (evidência/reprodutibilidade).</summary>
    public string BuildVersion { get; }

    /// <summary>Descrição do runtime (ex.: versão do .NET, RID).</summary>
    public string RuntimeDescription { get; }

    /// <summary>Perfil do host onde o benchmark rodou (ex.: <c>ci-shared</c>, <c>dedicated-inspector-8vcpu</c>).</summary>
    public string HostProfile { get; }

    /// <summary>Dataset sintético usado (sanitizado).</summary>
    public BenchmarkDatasetDescriptor Dataset { get; }

    /// <summary>Número de iterações de aquecimento (nunca entram em <see cref="Measurements"/>).</summary>
    public int WarmupIterations { get; }

    /// <summary>Número de iterações medidas.</summary>
    public int Iterations { get; }

    /// <summary>Medições, ordenadas por <see cref="BenchmarkMeasurement.IterationIndex"/>.</summary>
    public IReadOnlyList<BenchmarkMeasurement> Measurements => _measurements;

    /// <summary>Instante (UTC) em que a execução foi registrada.</summary>
    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>Uma linha persistida de <see cref="PerformanceBenchmarkRunRecord"/> viola um invariante estrutural do Domain.</summary>
public sealed class PerformanceBenchmarkRunIntegrityViolationException(string message) : Exception(message);
