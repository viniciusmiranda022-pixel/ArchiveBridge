using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Common;
using ArchiveBridge.Domain.Jobs;

namespace ArchiveBridge.Application.Jobs;

/// <summary>
/// Reagenda explicitamente uma nova tentativa de um Job em execução (Processing → RetryScheduled),
/// calculando o próximo instante pela política de retry a partir da contagem de tentativas atual.
/// Sob fencing por época. Distinto de <see cref="FailJobUseCase"/> (que decide retry vs. terminal
/// conforme a disposição da falha): aqui o reagendamento é a intenção direta.
/// </summary>
public sealed class ScheduleRetryUseCase(IJobStore store, IClock clock, RetryPolicy retryPolicy)
{
    private readonly IJobStore _store = store;
    private readonly IClock _clock = clock;
    private readonly RetryPolicy _retryPolicy = retryPolicy;

    /// <summary>Agenda a próxima tentativa com backoff; <see cref="JobCommandOutcome.FencedOut"/> se defasado.</summary>
    public async Task<JobCommandOutcome> ExecuteAsync(
        LeaseCommand command,
        ErrorCode error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var snapshot = await _store.GetAsync(command.Scope, command.JobId, cancellationToken).ConfigureAwait(false);
        var attemptCount = snapshot?.AttemptCount ?? 0;
        var nextAttempt = _retryPolicy.NextAttempt(attemptCount, _clock.UtcNow);
        return await _store.ScheduleRetryAsync(command, error, nextAttempt, cancellationToken).ConfigureAwait(false);
    }
}
