using ArchiveBridge.Contracts.Abstractions;

namespace ArchiveBridge.Infrastructure.Time;

/// <summary>Relógio de produção: lê o instante atual do sistema em UTC.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
