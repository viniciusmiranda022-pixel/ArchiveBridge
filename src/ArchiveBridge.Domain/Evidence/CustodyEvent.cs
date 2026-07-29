namespace ArchiveBridge.Domain.Evidence;

/// <summary>Evento de cadeia de custódia encadeado (skeleton; WORM/assinatura virão por slice).</summary>
public sealed record CustodyEvent(string EventName, DateTimeOffset OccurredAtUtc);
