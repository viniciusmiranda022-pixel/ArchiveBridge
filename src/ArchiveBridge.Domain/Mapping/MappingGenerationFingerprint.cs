using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Domain.Mapping;

/// <summary>
/// Impressão digital determinística de uma geração de mapping. Reúne TODOS os parâmetros que
/// influenciam o artefato produzido — não apenas a seleção. Duas gerações só são idempotentes (a
/// posterior reaproveita a versão anterior) quando as impressões coincidem por completo. Isto impede
/// o bug de "mesma seleção, code page diferente ⇒ documento novo devolvido com evidência antiga": se
/// qualquer parâmetro muda (configuração, seleção, pasta de destino, code page, esquema, gerador ou
/// política), a impressão muda e uma nova versão é gerada. Não contém segredo nem PII.
/// </summary>
public readonly record struct MappingGenerationFingerprint(Sha256Hash Value)
{
    /// <summary>
    /// Calcula a impressão a partir dos parâmetros de geração. A canonicalização é estável (ordem
    /// fixa de partes), então a mesma entrada produz sempre o mesmo hash.
    /// </summary>
    public static MappingGenerationFingerprint Compute(
        Sha256Hash configurationHash,
        Sha256Hash selectionHash,
        TargetRootFolder targetRootFolder,
        ContentCodePage contentCodePage,
        int schemaVersion,
        int generatorVersion,
        int policyVersion)
    {
        var hash = DeterministicHash.Compute(
        [
            nameof(MappingGenerationFingerprint),
            configurationHash.Value,
            selectionHash.Value,
            targetRootFolder.Value,
            contentCodePage.Value.ToString(CultureInfo.InvariantCulture),
            schemaVersion.ToString(CultureInfo.InvariantCulture),
            generatorVersion.ToString(CultureInfo.InvariantCulture),
            policyVersion.ToString(CultureInfo.InvariantCulture),
        ]);

        return new MappingGenerationFingerprint(hash);
    }
}
