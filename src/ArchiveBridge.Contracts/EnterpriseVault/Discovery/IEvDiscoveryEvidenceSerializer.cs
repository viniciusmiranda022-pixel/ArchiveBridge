using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>Bytes canônicos (determinísticos) da evidência de descoberta com o seu SHA-256.</summary>
public sealed record EvDiscoveryEvidenceBytes(ReadOnlyMemory<byte> Bytes, Sha256Hash ContentSha256);

/// <summary>
/// Serializa uma execução de descoberta na EVIDÊNCIA imutável canônica (JSON estável, determinístico —
/// sem timestamps voláteis, que ficam nos metadados SQL). O documento registra CLARAMENTE as impressões
/// digitais AUTORITATIVAS da reserva (<paramref name="configurationHash"/> e
/// <paramref name="semanticEvidenceHash"/> completos), separadas dos hashes INTERNOS do conjunto de
/// capacidades. O mesmo resultado semântico + as mesmas impressões produzem bytes byte-a-byte idênticos,
/// permitindo republicação idempotente e reconciliação; alterar um único campo muda os bytes e o hash.
/// </summary>
public interface IEvDiscoveryEvidenceSerializer
{
    /// <summary>
    /// Serializa o resultado na evidência canônica (bytes + hash determinístico), embutindo as impressões
    /// digitais autoritativas usadas pela reserva.
    /// </summary>
    EvDiscoveryEvidenceBytes Serialize(EvDiscoveryRunResult result, Sha256Hash configurationHash, Sha256Hash semanticEvidenceHash);
}
