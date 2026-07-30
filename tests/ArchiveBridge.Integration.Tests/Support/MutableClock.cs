using ArchiveBridge.Contracts.Abstractions;

namespace ArchiveBridge.Integration.Tests.Support;

/// <summary>Relógio de teste controlável: permite avançar o tempo de forma determinística.</summary>
public sealed class MutableClock(DateTimeOffset start) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = start;

    /// <summary>Avança o relógio.</summary>
    public void Advance(TimeSpan delta) => UtcNow += delta;

    /// <summary>Define o instante atual.</summary>
    public void Set(DateTimeOffset value) => UtcNow = value;
}
