using ArchiveBridge.Contracts.Abstractions;
using ArchiveBridge.Contracts.Jobs;
using ArchiveBridge.Domain.Jobs;
using ArchiveBridge.Domain.Operations;

namespace ArchiveBridge.Application.Jobs;

/// <summary>
/// Trata a falha de um Job. Falha transitória com tentativas disponíveis é reagendada com backoff
/// (Processing → RetryScheduled); caso contrário é terminal (Processing → Failed). A decisão usa a
/// contagem de tentativas persistida e é escrita sob fencing (época), evitando corrida.
/// </summary>
public sealed class FailJobUseCase(IJobStore store, IClock clock, RetryPolicy retryPolicy)
{
    private readonly IJobStore _store = store;
    private readonly IClock _clock = clock;
    private readonly RetryPolicy _retryPolicy = retryPolicy;

    /// <summary>Reagenda ou encerra o Job conforme a disposição e a política de retry.</summary>
    public async Task<JobCommandOutcome> ExecuteAsync(FailJobCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var lease = new LeaseCommand(command.Scope, command.JobId, command.Worker, command.Epoch, command.Correlation);

        if (command.Disposition == RetryDisposition.RetryTransient)
        {
            var snapshot = await _store.GetAsync(command.Scope, command.JobId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null && _retryPolicy.ShouldRetry(snapshot.AttemptCount))
            {
                var nextAttempt = _retryPolicy.NextAttempt(snapshot.AttemptCount, _clock.UtcNow);
                return await _store.ScheduleRetryAsync(lease, command.Error, nextAttempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return await _store.FailAsync(lease, command.Error, cancellationToken).ConfigureAwait(false);
    }
}
