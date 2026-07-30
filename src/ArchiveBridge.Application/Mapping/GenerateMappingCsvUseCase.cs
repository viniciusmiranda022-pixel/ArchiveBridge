using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.IdentityAndAccess;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Mapping;

/// <summary>Resultado de <see cref="GenerateMappingCsvUseCase"/>: Job durável, artefato e evidência de versão.</summary>
public sealed record MappingGenerationOutcome(JobId Job, MappingDocument Document, MappingCsvVersion Version);

/// <summary>
/// Comando GenerateMappingCsv: gera o CSV de mapping a partir de uma onda aprovada/congelada. Calcula
/// a próxima versão (N+1) sem sobrescrever a anterior; a persistência marca a versão utilizável
/// anterior como Superseded (evidência preservada). A geração é fail-closed: injeção de fórmula,
/// traversal, limite de 500 linhas, code page fora da política etc. fazem a operação falhar. O
/// artefato (bytes do CSV) é devolvido ao chamador; o SQL guarda apenas metadados e o sha256.
/// </summary>
public sealed class GenerateMappingCsvUseCase(IWaveStore waves, IMappingStore mappings, IJobStore jobs, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly IMappingStore _mappings = mappings;
    private readonly IJobStore _jobs = jobs;
    private readonly IClock _clock = clock;

    /// <summary>Gera e persiste a nova versão do mapping da onda.</summary>
    public async Task<MappingGenerationOutcome> ExecuteAsync(
        TenantScope scope,
        WaveId waveId,
        ContentCodePage contentCodePage,
        MappingPolicy policy,
        string generatedBy,
        CorrelationId correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        var max = await _mappings.GetMaxVersionAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
        var version = max == 0 ? MappingVersion.Initial : new MappingVersion(max).Next();

        var result = MappingCsvGenerator.Generate(wave, contentCodePage, policy, version, generatedBy, _clock.UtcNow);

        var jobId = await _jobs.CreateAsync(
            new CreateJobCommand(scope, Workload.Control, JobPriority.Normal, correlation), cancellationToken)
            .ConfigureAwait(false);
        await _mappings.SaveAsync(scope, result, cancellationToken).ConfigureAwait(false);

        return new MappingGenerationOutcome(jobId, result.Document, result.Version);
    }
}
