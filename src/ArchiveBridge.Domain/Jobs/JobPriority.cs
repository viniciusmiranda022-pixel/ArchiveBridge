namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Prioridade de reivindicação. Valor maior é reivindicado antes (a ordenação anti-starvation
/// combina prioridade desc., <c>NextAttemptAt</c> asc. e <c>CreatedAt</c> asc.).
/// </summary>
public readonly record struct JobPriority(int Value)
{
    /// <summary>Prioridade padrão.</summary>
    public static JobPriority Normal => new(0);
}
