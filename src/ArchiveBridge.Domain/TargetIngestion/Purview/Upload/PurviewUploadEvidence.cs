namespace ArchiveBridge.Domain.TargetIngestion.Purview.Upload;

/// <summary>
/// Evidência SANITIZADA do transporte AzCopy (item 10) — captura exit code, versão/hash do binário,
/// quantidade/tamanho de arquivos ESPERADOS (do plano server-side, nunca de output bruto do AzCopy
/// re-parseado) e o prefixo remoto usado. NUNCA carrega stdout/stderr bruto, SAS, path físico absoluto ou
/// qualquer valor que possa conter segredo/URL com query string — apenas o exit code estruturado e
/// contadores/identidades já conhecidos server-side.
/// </summary>
public sealed record PurviewUploadEvidence
{
    /// <summary>Cria a evidência, validando os contadores/tamanhos.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Contador de arquivos ou tamanho negativo/zero inválido.</exception>
    public PurviewUploadEvidence(
        AzCopyBinaryIdentity binary, int expectedFileCount, long expectedTotalBytes, PurviewRemoteUploadPrefix remotePrefix)
    {
        if (expectedFileCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedFileCount), expectedFileCount, "Ao menos um arquivo é esperado.");
        }

        if (expectedTotalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedTotalBytes), expectedTotalBytes, "Tamanho total esperado não pode ser negativo.");
        }

        Binary = binary;
        ExpectedFileCount = expectedFileCount;
        ExpectedTotalBytes = expectedTotalBytes;
        RemotePrefix = remotePrefix;
    }

    /// <summary>Identidade (versão + SHA-256) do binário AzCopy homologado que executou o transporte.</summary>
    public AzCopyBinaryIdentity Binary { get; }

    /// <summary>Quantidade de arquivos PST esperados (do conjunto canônico de bindings da onda), não do output do processo.</summary>
    public int ExpectedFileCount { get; }

    /// <summary>Soma dos tamanhos esperados, em bytes (do conjunto canônico de bindings da onda).</summary>
    public long ExpectedTotalBytes { get; }

    /// <summary>Prefixo remoto canônico usado neste transporte (metadado NÃO secreto).</summary>
    public PurviewRemoteUploadPrefix RemotePrefix { get; }
}
