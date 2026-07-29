namespace ArchiveBridge.Domain.Jobs;

/// <summary>Identidade de um Job (value object).</summary>
public readonly record struct JobId(Guid Value)
{
    /// <summary>Gera uma nova identidade de Job.</summary>
    public static JobId New() => new(Guid.NewGuid());
}
