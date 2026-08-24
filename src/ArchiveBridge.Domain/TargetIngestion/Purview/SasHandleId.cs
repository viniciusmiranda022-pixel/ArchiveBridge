namespace ArchiveBridge.Domain.TargetIngestion.Purview;

/// <summary>Identidade do handle opaco de custódia de um SAS (work order AB-I5-004 item 8), gerada pelo servidor.</summary>
public readonly record struct SasHandleId(Guid Value)
{
    /// <summary>Gera uma nova identidade.</summary>
    public static SasHandleId New() => new(Guid.NewGuid());
}
