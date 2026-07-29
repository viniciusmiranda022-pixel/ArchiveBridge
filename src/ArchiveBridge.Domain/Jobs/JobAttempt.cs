namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Uma tentativa de execução de um Job (registrada em <c>job_attempts</c>). Cada reivindicação
/// abre uma tentativa com a época do lease correspondente.
/// </summary>
public sealed record JobAttempt(
    int AttemptNumber,
    WorkerId Worker,
    LeaseEpoch LeaseEpoch,
    DateTimeOffset StartedAtUtc);
