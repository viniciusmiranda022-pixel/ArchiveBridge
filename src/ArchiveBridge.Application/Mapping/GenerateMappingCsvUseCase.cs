using ArchiveBridge.Application.Planning;
using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.Waves;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.Mapping;

/// <summary>
/// Resultado de <see cref="GenerateMappingCsvUseCase"/>: o artefato, a evidência de versão persistida
/// e se houve regeneração (falso quando o retry reaproveitou uma versão utilizável idêntica).
/// </summary>
public sealed record MappingGenerationOutcome(MappingDocument Document, MappingCsvVersion Version, bool Regenerated);

/// <summary>
/// Corpo de execução do comando durável <c>GenerateMappingCsv</c>. Gera o CSV a partir de uma onda
/// aprovada/congelada (fail-closed: injeção de fórmula, traversal, limite de 500 linhas, code page
/// fora da política etc. fazem a operação falhar). A versão N+1 é atribuída atomicamente na
/// persistência, sem sobrescrever a anterior (marcada Superseded). É idempotente em retry: se já há
/// versão utilizável para a MESMA seleção, não gera outra. O artefato (bytes) é devolvido ao chamador;
/// o SQL guarda apenas metadados e o sha256.
/// </summary>
public sealed class GenerateMappingCsvUseCase(IWaveStore waves, IMappingStore mappings, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly IMappingStore _mappings = mappings;
    private readonly IClock _clock = clock;

    /// <summary>Gera (ou reaproveita) a versão do mapping da onda.</summary>
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
        _ = correlation;
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        var result = MappingCsvGenerator.Generate(
            wave, contentCodePage, policy, MappingVersion.Initial, generatedBy, _clock.UtcNow);

        var usable = await _mappings.GetUsableAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
        if (usable is { } current && string.Equals(
                current.SelectionHash.Value, wave.SelectionHash.Value, StringComparison.Ordinal))
        {
            return new MappingGenerationOutcome(result.Document, current, Regenerated: false);
        }

        var persisted = await _mappings.SaveAsync(scope, result, cancellationToken).ConfigureAwait(false);
        return new MappingGenerationOutcome(result.Document, persisted, Regenerated: true);
    }
}
