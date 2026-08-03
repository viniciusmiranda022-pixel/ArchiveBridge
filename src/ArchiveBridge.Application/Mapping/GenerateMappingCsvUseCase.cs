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
/// persistência, sem sobrescrever a anterior (marcada Superseded), e o artefato imutável (mapping.csv
/// + mapping.sha256 + manifesto) é PUBLICADO no armazenamento de artefatos antes do commit — uma
/// versão persistida sempre tem artefato. É idempotente pela IMPRESSÃO DIGITAL COMPLETA de geração
/// (<see cref="MappingGenerationFingerprint"/>): só reaproveita quando TODOS os parâmetros coincidem
/// (configuração, seleção, pasta de destino, code page, esquema, gerador e política) — ao reaproveitar,
/// devolve exatamente o artefato e a evidência daquela versão (o mesmo SHA-256), nunca um documento
/// recém-gerado com evidência antiga.
/// </summary>
public sealed class GenerateMappingCsvUseCase(
    IWaveStore waves, IMappingStore mappings, IMappingArtifactStore artifacts, IClock clock)
{
    private readonly IWaveStore _waves = waves;
    private readonly IMappingStore _mappings = mappings;
    private readonly IMappingArtifactStore _artifacts = artifacts;
    private readonly IClock _clock = clock;

    /// <summary>Gera (ou reaproveita) a versão do mapping da onda. Quando <paramref name="fence"/> é informado, o efeito é cercado pelo Job.</summary>
    public async Task<MappingGenerationOutcome> ExecuteAsync(
        TenantScope scope,
        WaveId waveId,
        ContentCodePage contentCodePage,
        MappingPolicy policy,
        string generatedBy,
        CorrelationId correlation,
        CancellationToken cancellationToken,
        JobFence? fence = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = correlation;
        var wave = await _waves.GetAsync(scope, waveId, cancellationToken).ConfigureAwait(false)
            ?? throw new PlanningNotFoundException("Onda não encontrada no escopo.");

        var result = MappingCsvGenerator.Generate(
            wave, contentCodePage, policy, MappingVersion.Initial, generatedBy, _clock.UtcNow);
        var fingerprint = result.Version.Fingerprint;

        var usable = await _mappings.GetUsableAsync(scope, waveId, cancellationToken).ConfigureAwait(false);
        if (usable is { } current && current.Fingerprint == fingerprint)
        {
            // Reaproveitamento idempotente: devolve o artefato EXATO daquela versão (conjunto validado
            // e bytes conferidos contra o SHA-256 da evidência), nunca o documento recém-gerado.
            var content = await _artifacts
                .GetAsync(new MappingArtifactDescriptor(scope, waveId, current.Version), cancellationToken).ConfigureAwait(false)
                ?? throw new MappingGenerationException(
                    "Versão utilizável sem artefato persistido; evidência inconsistente (fail-closed).");
            var persistedDocument = MappingDocument.FromPersisted(content.Bytes, current.RowCount, current.ContentSha256);
            return new MappingGenerationOutcome(persistedDocument, current, Regenerated: false);
        }

        // Fase 1 (fora da transação): encena o artefato. Fase 2 (dentro da transação curta): publica.
        var staging = await _artifacts
            .StageAsync(result.Document.GetBytes(), result.Document.ContentSha256, cancellationToken).ConfigureAwait(false);
        try
        {
            var persisted = await _mappings.SaveAsync(
                scope,
                result,
                fence,
                (version, ct) => _artifacts.PublishAsync(staging, new MappingArtifactDescriptor(scope, waveId, version), ct),
                cancellationToken).ConfigureAwait(false);

            return new MappingGenerationOutcome(result.Document, persisted, Regenerated: true);
        }
        catch
        {
            // Rollback: um staging não publicado (a publicação renomeia o diretório) é descartado.
            await _artifacts.DiscardAsync(staging, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
