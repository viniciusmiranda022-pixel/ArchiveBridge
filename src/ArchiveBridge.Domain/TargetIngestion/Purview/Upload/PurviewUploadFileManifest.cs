using System.Globalization;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.PstProcessing;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// UM item da manifestação determinística por arquivo (AB-I5-015 item 1): a identidade canônica do PST
/// individual efetivamente transportado nesta tentativa — a execução de partição que o produziu (mesma
/// referência já usada por <see cref="WavePartitionBindings.WavePartitionOutputBinding.Execution"/>, nunca
/// um índice/ordinal solto), o nome remoto EXATO usado pelo AzCopy real e o hash/tamanho canônicos do
/// output. Nenhum caminho físico/local, mailbox, UPN ou segredo — apenas identidades e evidência já
/// conhecidas server-side (mesmo princípio sanitizado de <see cref="PurviewUploadEvidence"/>).
/// </summary>
public sealed record PurviewUploadFileManifestItem
{
    /// <summary>Cria um item, validando o tamanho esperado.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedSizeBytes"/> é negativo.</exception>
    public PurviewUploadFileManifestItem(
        PartitionExecutionId execution, PurviewRemotePstName remoteName, Sha256Hash outputHash, long expectedSizeBytes)
    {
        if (expectedSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes), expectedSizeBytes, "Tamanho esperado não pode ser negativo.");
        }

        Execution = execution;
        RemoteName = remoteName;
        OutputHash = outputHash;
        ExpectedSizeBytes = expectedSizeBytes;
    }

    /// <summary>
    /// A execução de partição canônica que produziu este arquivo — a MESMA referência que
    /// <see cref="WavePartitionBindings.WavePartitionOutputBinding.Execution"/> carrega para o vínculo desta
    /// linha; correlaciona univocamente este item a UM binding/destino dentro da wave.
    /// </summary>
    public PartitionExecutionId Execution { get; }

    /// <summary>Nome de arquivo remoto EXATO usado pelo transporte real (mesma função pura do upload).</summary>
    public PurviewRemotePstName RemoteName { get; }

    /// <summary>Hash SHA-256 canônico do output no momento do transporte.</summary>
    public Sha256Hash OutputHash { get; }

    /// <summary>Tamanho esperado, em bytes, do output canônico.</summary>
    public long ExpectedSizeBytes { get; }
}

/// <summary>
/// Hash determinístico da manifestação completa (AB-I5-015 item 2) — participa da evidência persistida da
/// tentativa e é revalidado a cada leitura (mesmo princípio de <c>binding_hash</c>/<c>handle_hash</c>):
/// adulterar qualquer item (inclusive inserir/remover/duplicar) é detectado fail-closed.
/// </summary>
public static class PurviewUploadFileManifestHash
{
    private const string HashPrefix = "archivebridge.purview.upload-file-manifest.v1";

    /// <summary>
    /// Calcula o hash a partir do conjunto de itens, ordenado deterministicamente por
    /// <see cref="PurviewUploadFileManifestItem.Execution"/> — nunca pela ordem de leitura/inserção, que
    /// pode variar entre tentativas sem que o conteúdo real tenha mudado (mesmo princípio de
    /// <see cref="PurviewUploadRequestIdentity"/>).
    /// </summary>
    public static Sha256Hash Compute(IReadOnlyList<PurviewUploadFileManifestItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("Ao menos um item é obrigatório para calcular o hash da manifestação.", nameof(items));
        }

        var parts = new List<string> { HashPrefix, items.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var item in items.OrderBy(item => item.Execution.Value))
        {
            parts.Add(item.Execution.Value.ToString("N"));
            parts.Add(item.RemoteName.Value);
            parts.Add(item.OutputHash.Value);
            parts.Add(item.ExpectedSizeBytes.ToString(CultureInfo.InvariantCulture));
        }

        return DeterministicHash.Compute(parts);
    }
}
