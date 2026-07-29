using ArchiveBridge.Domain.Projects;

namespace ArchiveBridge.Domain.Waves;

public readonly record struct WaveId(Guid Value);

/// <summary>Onda de migração: conjunto fechado de artefatos aprovados (skeleton).</summary>
public sealed class MigrationWave(WaveId id, ProjectId project)
{
    public WaveId Id { get; } = id;
    public ProjectId Project { get; } = project;
}
