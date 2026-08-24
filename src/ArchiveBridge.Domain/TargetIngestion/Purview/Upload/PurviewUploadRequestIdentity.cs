using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.WavePartitionBindings;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Identidade lógica DETERMINÍSTICA de um upload (AB-I5-009 item 14): mesma onda + mesmo conjunto canônico
/// de bindings/outputs (item 9 do vínculo AB-I5-010) + mesma geração de SAS/destino + mesma
/// política/configuração (binário AzCopy homologado + prefixo remoto) converge para a MESMA identidade —
/// mudança real em qualquer componente produz uma identidade NOVA. Usada pelo processador de comando para
/// decidir, a cada tentativa, se um <c>Uploaded</c> já persistido é um réplay idempotente legítimo (mesma
/// identidade — não reexecuta AzCopy) ou se as entradas mudaram desde a última tentativa (identidade
/// diferente — nunca um falso réplay).
/// </summary>
public static class PurviewUploadRequestIdentity
{
    private const string HashPrefix = "archivebridge.purview.upload-request.v1";

    /// <summary>
    /// Calcula a identidade a partir do conjunto (ordenado deterministicamente por <c>PartKey</c> — nunca
    /// pela ordem de leitura do store) de bindings canônicos, da geração do handle SAS adquirido, do
    /// binário AzCopy homologado observado e do prefixo remoto.
    /// </summary>
    public static Sha256Hash Compute(
        IReadOnlyList<WavePartitionOutputBinding> bindings,
        Guid sasHandleId,
        int sasGeneration,
        AzCopyBinaryIdentity binary,
        PurviewRemoteUploadPrefix remotePrefix)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0)
        {
            throw new ArgumentException("Ao menos um binding canônico é obrigatório para calcular a identidade.", nameof(bindings));
        }

        var parts = new List<string>
        {
            HashPrefix,
            sasHandleId.ToString("N"),
            sasGeneration.ToString(CultureInfo.InvariantCulture),
            binary.Version,
            binary.Sha256.Value,
            remotePrefix.Value,
            bindings.Count.ToString(CultureInfo.InvariantCulture),
        };

        // Ordenação determinística e ESTÁVEL por Execution.Value (sempre único — um novo GUID por linha de
        // execução, nunca reaproveitado) — nunca a ordem de leitura do store, que pode variar entre
        // tentativas sem que o conteúdo real tenha mudado. PartKey sozinho não seria suficiente: embora
        // improvável na prática, duas execuções distintas poderiam colidir em PartKey (mesmo planHash e
        // sequência); ordenar por Execution.Value garante uma ordem total mesmo nesse caso.
        foreach (var binding in bindings.OrderBy(binding => binding.Execution.Value))
        {
            parts.Add(binding.Execution.Value.ToString("N"));
            parts.Add(binding.PartKey.Value);
            parts.Add(binding.OutputHash.Value);
            parts.Add(binding.OutputSizeBytes.ToString(CultureInfo.InvariantCulture));
        }

        return DeterministicHash.Compute(parts);
    }
}
