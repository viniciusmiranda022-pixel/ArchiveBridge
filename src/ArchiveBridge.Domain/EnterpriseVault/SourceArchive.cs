namespace ArchiveBridge.Domain.EnterpriseVault;

public readonly record struct SourceArchiveId(string Value);

/// <summary>Archive de origem no Enterprise Vault (skeleton). Versão não implica suporte (ADR-0013).</summary>
public sealed class SourceArchive(SourceArchiveId id)
{
    public SourceArchiveId Id { get; } = id;
}
