using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Contracts.Mapping;
using ArchiveBridge.Contracts.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Application.TargetIngestion.Purview.MappingCsv;

/// <summary>Conteúdo exato/versionado do CSV, já revalidado contra a evidência SQL, pronto para ser servido.</summary>
public sealed record PurviewMappingCsvDownload(PurviewMappingCsvVersion Version, byte[] Bytes);

/// <summary>
/// Obtém o CSV do Purview publicado de uma onda por referência OPACA tenant/project/wave-scoped
/// (AB-I5-012 item 13): (<see cref="WaveId"/>, <see cref="MappingVersion"/>) — nunca por caminho físico.
/// Anti-IDOR: onda/versão inexistente OU de outro escopo produzem o MESMO erro
/// (<see cref="PurviewMappingCsvSourceNotFoundException"/>), nunca revelando qual causa. Serve o conteúdo
/// EXATO já validado (a evidência SQL e o artefato imutável são cruzados; divergência falha fechada); NUNCA
/// regenera implicitamente — uma versão baixada é sempre a evidência histórica publicada, mesmo depois de
/// substituída por uma versão mais nova (preservada, nunca apagada — runbook §25.9).
/// </summary>
public sealed class DownloadPurviewMappingCsvUseCase(IPurviewMappingCsvStore mappings, IMappingArtifactStore artifacts)
{
    private const string NotFoundMessage =
        "Download recusado (fail-closed): versão de mapping do Purview inexistente/fora do escopo autorizado.";

    private readonly IPurviewMappingCsvStore _mappings = mappings;
    private readonly IMappingArtifactStore _artifacts = artifacts;

    /// <exception cref="PurviewMappingCsvSourceNotFoundException">Versão inexistente/fora do escopo (anti-IDOR).</exception>
    /// <exception cref="PurviewMappingCsvGenerationException">Artefato ausente ou hash divergente da evidência SQL (fail-closed).</exception>
    public async Task<PurviewMappingCsvDownload> ExecuteAsync(
        TenantScope scope, WaveId waveId, MappingVersion version, CancellationToken cancellationToken)
    {
        var metadata = await _mappings.GetByVersionAsync(scope, waveId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewMappingCsvSourceNotFoundException(NotFoundMessage);

        var descriptor = new MappingArtifactDescriptor(scope, waveId, version);
        var content = await _artifacts.GetAsync(descriptor, cancellationToken).ConfigureAwait(false)
            ?? throw new PurviewMappingCsvGenerationException(
                "Artefato ausente para uma versão com evidência SQL (fail-closed).");

        if (content.ContentSha256 != metadata.ContentSha256)
        {
            throw new PurviewMappingCsvGenerationException(
                "O hash do artefato publicado diverge da evidência SQL (fail-closed).");
        }

        return new PurviewMappingCsvDownload(metadata, content.Bytes.ToArray());
    }
}
