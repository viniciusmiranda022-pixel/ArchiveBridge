using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Evidência SANITIZADA do transporte AzCopy (item 10) — captura exit code, versão/hash do binário, o
/// prefixo remoto usado e a manifestação determinística por arquivo (AB-I5-015 item 1:
/// <see cref="Manifest"/>) — a identidade canônica ordenada de CADA PST efetivamente transportado nesta
/// tentativa (execução/binding, nome remoto, hash e tamanho). <see cref="ExpectedFileCount"/> e
/// <see cref="ExpectedTotalBytes"/> são SEMPRE derivados do <see cref="Manifest"/> (nunca informados
/// independentemente) — uma única fonte de verdade, eliminando a possibilidade de dois conjuntos de PSTs
/// diferentes coincidirem em contagem/soma de bytes e ainda assim satisfazerem a evidência (AB-I5-015 item
/// 5). NUNCA carrega stdout/stderr bruto, SAS, path físico absoluto ou qualquer valor que possa conter
/// segredo/URL com query string — apenas o exit code estruturado e identidades/contadores já conhecidos
/// server-side.
/// </summary>
public sealed record PurviewUploadEvidence
{
    /// <summary>
    /// Cria a evidência a partir da manifestação por arquivo, validando-a e derivando os agregados/hash.
    /// </summary>
    /// <exception cref="ArgumentException">Manifestação vazia ou com execução duplicada.</exception>
    public PurviewUploadEvidence(
        AzCopyBinaryIdentity binary, PurviewRemoteUploadPrefix remotePrefix, IReadOnlyList<PurviewUploadFileManifestItem> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Count == 0)
        {
            throw new ArgumentException("Ao menos um item de manifestação é esperado.", nameof(manifest));
        }

        var ordered = manifest.OrderBy(item => item.Execution.Value).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Execution == ordered[index - 1].Execution)
            {
                throw new ArgumentException(
                    "A manifestação não pode conter mais de um item para a mesma execução (fail-closed).", nameof(manifest));
            }
        }

        Binary = binary;
        RemotePrefix = remotePrefix;
        Manifest = ordered;
        ManifestHash = PurviewUploadFileManifestHash.Compute(ordered);
        ExpectedFileCount = ordered.Length;
        ExpectedTotalBytes = ordered.Sum(item => item.ExpectedSizeBytes);
    }

    /// <summary>Identidade (versão + SHA-256) do binário AzCopy homologado que executou o transporte.</summary>
    public AzCopyBinaryIdentity Binary { get; }

    /// <summary>
    /// Manifestação determinística por arquivo, ordenada canonicamente por
    /// <see cref="PurviewUploadFileManifestItem.Execution"/> — a prova, item a item, de exatamente QUAIS PSTs
    /// este transporte comprovadamente cobriu (AB-I5-015 item 4: exigida para correspondência exata 1:1 com
    /// cada binding/execução atual antes de gerar o mapping CSV).
    /// </summary>
    public IReadOnlyList<PurviewUploadFileManifestItem> Manifest { get; }

    /// <summary>Hash determinístico da manifestação completa — revalidado a cada leitura persistida (fail-closed sob tampering).</summary>
    public Sha256Hash ManifestHash { get; }

    /// <summary>Quantidade de arquivos PST cobertos por este transporte, derivada do <see cref="Manifest"/>.</summary>
    public int ExpectedFileCount { get; }

    /// <summary>Soma dos tamanhos esperados, em bytes, derivada do <see cref="Manifest"/>.</summary>
    public long ExpectedTotalBytes { get; }

    /// <summary>Prefixo remoto canônico usado neste transporte (metadado NÃO secreto).</summary>
    public PurviewRemoteUploadPrefix RemotePrefix { get; }
}
