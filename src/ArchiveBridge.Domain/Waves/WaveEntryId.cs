using System.Globalization;
using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.Waves;

/// <summary>
/// Identidade OPACA e DETERMINÍSTICA de uma <see cref="WaveEntry"/> dentro de uma onda (AB-I5-013 item 1).
/// Nunca um índice/ordinal: é uma função pura <c>(WaveId, WaveEntry)</c> → hash, derivada exclusivamente
/// dos campos IMUTÁVEIS já validados de <see cref="WaveEntry"/> mais a identidade da onda à qual a
/// entrada pertence. Duas chamadas com o MESMO conteúdo produzem sempre o MESMO valor — permite a um
/// vínculo externo (ex.: <see cref="WavePartitionBindings.WavePartitionOutputBinding"/>) referenciar uma
/// entrada por este ID opaco enquanto o servidor RECOMPUTA e valida a associação sob demanda a partir da
/// seleção corrente, sem exigir nenhuma coluna/estado adicional persistido em <c>wave_entries</c> e sem
/// alterar a semântica de <see cref="WaveSelection"/> (não é uma chave armazenada na própria entrada).
/// </summary>
public readonly record struct WaveEntryId(Sha256Hash Value)
{
    /// <summary>Deriva a identidade opaca de uma entrada dentro de uma onda específica.</summary>
    public static WaveEntryId Derive(WaveId wave, WaveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new WaveEntryId(DeterministicHash.Compute(
        [
            nameof(WaveEntryId),
            wave.Value.ToString("N"),
            entry.FilePath,
            entry.PstName,
            entry.Archive.Identity.Value,
            entry.Archive.Mailbox,
            entry.SizeBytes.ToString(CultureInfo.InvariantCulture),
            entry.ItemCount.ToString(CultureInfo.InvariantCulture),
        ]));
    }
}
