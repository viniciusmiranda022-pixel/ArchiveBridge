namespace ArchiveBridge.Domain.Jobs;

/// <summary>
/// Descreve uma transição de estado a ser registrada na auditoria. Retornado pelos métodos do
/// agregado <see cref="Job"/> para que a camada durável persista a trilha de forma atômica.
/// </summary>
public sealed record JobTransition(
    JobId JobId,
    JobState FromState,
    JobState ToState,
    ReasonCode Reason,
    LeaseEpoch LeaseEpoch,
    WorkerId? Worker,
    DateTimeOffset OccurredAtUtc);
