namespace ArchiveBridge.Infrastructure.PstProcessing;

/// <summary>
/// Configuração da raiz de custódia local de PSTs e dos limites fail-closed de recurso/tempo aplicados
/// pela engine (§7 do work order — nunca ilimitado). <see cref="RootPath"/> é SEMPRE do servidor
/// (configuração de implantação), nunca de um valor de requisição.
/// </summary>
public sealed class PstStorageOptions
{
    /// <summary>Raiz NTFS/NAS/SMB sob a qual todo caminho relativo de custódia é resolvido e contido.</summary>
    public required string RootPath { get; init; }

    /// <summary>Tamanho máximo aceito para inspeção (proteção contra exaustão de recursos). Default: 200 GiB.</summary>
    public long MaxSizeBytes { get; init; } = 200L * 1024 * 1024 * 1024;

    /// <summary>Tempo máximo de uma inspeção antes de abortar fail-closed. Default: 10 minutos.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}
