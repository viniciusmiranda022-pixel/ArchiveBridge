using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Mapping;
using ArchiveBridge.Domain.Waves;

namespace ArchiveBridge.Contracts.Mapping;

/// <summary>Identifica de forma versionada o artefato de uma geração (tenant/projeto/onda/versão).</summary>
public sealed record MappingArtifactDescriptor(TenantScope Scope, WaveId Wave, MappingVersion Version);

/// <summary>
/// Referência lógica ao artefato imutável persistido: o caminho lógico (versionado, relativo à raiz
/// configurada), o tamanho em bytes e o SHA-256 do conteúdo. É isso — e não os bytes — que o SQL
/// guarda como metadado.
/// </summary>
public sealed record MappingArtifactReference(string LogicalPath, long SizeBytes, Sha256Hash ContentSha256);

/// <summary>Conteúdo lido de volta do artefato imutável (bytes + evidência de integridade).</summary>
public sealed record MappingArtifactContent(ReadOnlyMemory<byte> Bytes, Sha256Hash ContentSha256);

/// <summary>
/// Porta do armazenamento IMUTÁVEL de artefatos de mapping (baseline on-premises: filesystem local
/// configurável; SMB/NAS opcional; sem dependência de Azure). Cada geração persiste atomicamente o
/// <c>mapping.csv</c>, o <c>mapping.sha256</c> e um manifesto de evidência, por um caminho versionado
/// por tenant/projeto/onda/versão. A publicação é atômica (escrita em temporário → flush → rename) e
/// NUNCA sobrescreve uma versão anterior: republicar a MESMA versão só é aceito se os bytes coincidem
/// (idempotente); bytes divergentes para a mesma versão são recusados. Não guarda segredo/PII (o CSV
/// contém apenas metadados de planejamento).
/// </summary>
public interface IMappingArtifactStore
{
    /// <summary>
    /// Publica o artefato da versão informada de forma atômica e idempotente. Retorna a referência
    /// lógica (caminho + tamanho + hash). Falha antes do rename não publica artefato parcial; a versão
    /// anterior permanece intacta.
    /// </summary>
    Task<MappingArtifactReference> SaveAsync(
        MappingArtifactDescriptor descriptor,
        ReadOnlyMemory<byte> csvBytes,
        Sha256Hash contentSha256,
        CancellationToken cancellationToken);

    /// <summary>Lê o artefato pelo caminho lógico persistido; <see langword="null"/> se ausente.</summary>
    Task<MappingArtifactContent?> GetAsync(string logicalPath, CancellationToken cancellationToken);
}
