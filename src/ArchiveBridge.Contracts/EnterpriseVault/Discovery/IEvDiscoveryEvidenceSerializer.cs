using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.EnterpriseVault.Discovery;

namespace ArchiveBridge.Contracts.EnterpriseVault.Discovery;

/// <summary>Bytes canônicos (determinísticos) da evidência de descoberta com o seu SHA-256.</summary>
public sealed record EvDiscoveryEvidenceBytes(ReadOnlyMemory<byte> Bytes, Sha256Hash ContentSha256);

/// <summary>
/// Serializa uma execução de descoberta na EVIDÊNCIA imutável canônica (JSON estável, determinístico —
/// sem timestamps voláteis, que ficam nos metadados SQL). O mesmo resultado semântico produz bytes
/// byte-a-byte idênticos, permitindo republicação idempotente e reconciliação. Alterar um único campo
/// de capacidade muda os bytes e o hash.
/// </summary>
public interface IEvDiscoveryEvidenceSerializer
{
    /// <summary>Serializa o resultado na evidência canônica (bytes + hash determinístico).</summary>
    EvDiscoveryEvidenceBytes Serialize(EvDiscoveryRunResult result);
}
