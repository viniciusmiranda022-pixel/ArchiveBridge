using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.MappingCsv;

/// <summary>
/// Impressão digital determinística de uma geração do mapping CSV do Purview. Reúne TODOS os parâmetros
/// que influenciam o artefato produzido: a onda, a pasta de destino, o CONTEÚDO ordenado das linhas (que
/// já embute vínculo/execução/mailbox/archive resolvidos) e a identidade da tentativa de upload verificada
/// que autoriza a geração — nunca apenas a seleção da onda (mesmo princípio de
/// <see cref="Mapping.MappingGenerationFingerprint"/>, item 8, adaptado à fonte evidence-driven deste
/// Passo). Duas gerações só são idempotentes quando as impressões coincidem por completo: se qualquer
/// vínculo, execução, precheck de mailbox ou a própria evidência de upload mudar, a impressão muda e uma
/// nova versão é gerada — nunca reaproveita silenciosamente evidência desatualizada. Não contém segredo
/// nem PII (o conteúdo das linhas entra apenas pelo seu hash agregado, nunca em claro).
/// </summary>
public readonly record struct PurviewMappingGenerationFingerprint(Sha256Hash Value)
{
    /// <summary>Calcula a impressão a partir dos parâmetros de geração e do hash agregado das linhas ordenadas.</summary>
    public static PurviewMappingGenerationFingerprint Compute(
        WaveId wave,
        TargetRootFolder targetRootFolder,
        Sha256Hash orderedRowsHash,
        Sha256Hash uploadAttemptIdentityHash,
        int schemaVersion,
        int generatorVersion)
    {
        var hash = DeterministicHash.Compute(
        [
            nameof(PurviewMappingGenerationFingerprint),
            wave.Value.ToString("N"),
            targetRootFolder.Value,
            orderedRowsHash.Value,
            uploadAttemptIdentityHash.Value,
            schemaVersion.ToString(CultureInfo.InvariantCulture),
            generatorVersion.ToString(CultureInfo.InvariantCulture),
        ]);

        return new PurviewMappingGenerationFingerprint(hash);
    }
}
