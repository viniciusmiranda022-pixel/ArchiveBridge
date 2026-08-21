namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Configuração da raiz de output controlado das partes materializadas (Slice 4B, Passo 3) e dos limites
/// fail-closed de recurso/tempo aplicados pelo writer (nunca ilimitado). <see cref="RootPath"/> é SEMPRE do
/// servidor (configuração de implantação), nunca de um valor de requisição — mesmo padrão de
/// <see cref="PstStorageOptions.RootPath"/> para a raiz de custódia de origem.
/// </summary>
public sealed class PartitionExecutionOutputOptions
{
    /// <summary>Raiz NTFS/NAS/SMB sob a qual todo output de partição é escrito e contido.</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Margem de segurança adicional, além do tamanho da origem, exigida como espaço livre no preflight
    /// antes de iniciar a cópia. Default: 512 MiB.
    /// </summary>
    public long MinFreeSpaceMarginBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Tempo máximo de uma execução (cópia + verificação) antes de abortar fail-closed. Default: 2 horas.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(2);
}
