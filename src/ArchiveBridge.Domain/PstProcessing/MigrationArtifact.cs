using ArchiveBridge.Domain.Common;

namespace ArchiveBridge.Domain.PstProcessing;

public readonly record struct ArtifactId(Guid Value);

/// <summary>Artefato imutável (hash + tamanho + lineage). Nunca sobrescrito após aprovado (skeleton).</summary>
public sealed class MigrationArtifact(ArtifactId id, Sha256Hash hash, long sizeBytes)
{
    public ArtifactId Id { get; } = id;
    public Sha256Hash Hash { get; } = hash;
    public long SizeBytes { get; } = sizeBytes;
}
