namespace ArchiveBridge.Domain.Common;

/// <summary>Correlaciona operações e transições na trilha de auditoria (value object).</summary>
public readonly record struct CorrelationId(Guid Value)
{
    /// <summary>Gera uma nova correlação.</summary>
    public static CorrelationId New() => new(Guid.NewGuid());
}
